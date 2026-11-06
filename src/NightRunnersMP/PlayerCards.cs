using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using UnityEngine;

namespace NightRunnersMP;

// what a card shows besides name and car, each player sends their own
internal sealed class PlayerStats
{
    public int Rep;
    public int Hp;
    public float Handling;
    public float Braking;
    public float SpecFloor;
    public string Crew = "";

    // from the server's crew list, goes out with the stats
    public static string OwnCrew = "";

    public static string OwnTag(string name) => OwnCrew.Length > 0 ? $"[{OwnCrew}] {name}" : name;

    public static PlayerStats Read(GodConstant god, CarLocalCustom car)
    {
        var data = car.carData;
        return new PlayerStats
        {
            Rep = god.player_gameRep,
            Hp = data != null ? data.carHP : 0,
            Handling = data != null ? data.outSideGarage_handlingRatingCache : 0f,
            Braking = data != null ? data.outSideGarage_brakingRatingCache : 0f,
            SpecFloor = data != null ? data.getOriginSpecFloor() : 0f,
            Crew = OwnCrew,
        };
    }

    public byte[] Write()
    {
        var w = new NetDataWriter();
        w.Put(Rep);
        w.Put(Hp);
        w.Put(Handling);
        w.Put(Braking);
        w.Put(SpecFloor);
        w.Put(Crew);
        return w.CopyData();
    }

    public static PlayerStats Parse(byte[] bytes)
    {
        var r = new NetDataReader(bytes);
        return new PlayerStats
        {
            Rep = r.GetInt(),
            Hp = r.GetInt(),
            Handling = r.GetFloat(),
            Braking = r.GetFloat(),
            SpecFloor = r.GetFloat(),
            Crew = SafeText.Clean(r.GetString(), Protocol.MaxCrewLength),
        };
    }
}

// the card floating by a racer's car. second copy of the same prefab, filled like meetSpot_Core does, shown when looking at a player's car on foot
internal sealed class PlayerCards
{
    private const float Range = 14f;
    private const float AimDot = 0.94f;
    private const float Height = 1f;
    private const float FadePerSecond = 4f;
    private const string NoCrew = "NO CREW";

    private readonly ManualLogSource _log;
    private meetSpot _spot;
    private RectTransform _card;
    private meetSpot_3DUI _ui;
    private CanvasGroup _group;
    private Quaternion _textTurn = Quaternion.identity;
    private Remote _target;
    private PlayerStats _shownStats;
    private GameObject _shownCar;
    private float _alpha;
    private bool _failed;

    public PlayerCards(ManualLogSource log) => _log = log;

    public void Tick(Parking parking, IEnumerable<Remote> remotes)
    {
        if (_failed)
            return;
        try
        {
            Update(parking, remotes);
        }
        catch (System.Exception e)
        {
            _log.LogError($"Player cards turned off: {e}");
            _failed = true;
        }
    }

    private void Update(Parking parking, IEnumerable<Remote> remotes)
    {
        var spot = parking.CurrentSpot;
        if (spot == null || (_spot != null && _spot.Pointer != spot.Pointer))
            Drop();
        if (spot == null)
            return;
        if (_card == null && !Create(spot))
            return;

        var target = parking.TryReadWalker(out _, out _) ? Aimed(remotes) : null;
        Fade(target != null);
        if (target == null)
            return;
        if (target != _target || target.Stats != _shownStats || target.Car != _shownCar)
            Fill(target);
        _target = target;
        var position = target.Car.transform.position;
        _card.position = new Vector3(position.x, position.y + Height, position.z);
        Face();
    }

    // world space text only reads from one side, keep that side to the camera
    private void Face()
    {
        var camera = Camera.main;
        if (camera == null)
            return;
        var away = _card.position - camera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.01f)
            _card.rotation = Quaternion.LookRotation(away) * _textTurn;
    }

    private bool Create(meetSpot spot)
    {
        if (spot.aiRacer_info_prefab == null || spot.aiRacer_info == null)
            return false;
        _spot = spot;
        _card = Object.Instantiate(spot.aiRacer_info_prefab, spot.aiRacer_info.parent, true);
        _card.name = "NRMP player card";
        _ui = _card.GetComponent<meetSpot_3DUI>();
        _group = _card.GetComponent<CanvasGroup>();
        if (_ui == null || _group == null)
        {
            _log.LogError("The racer card isn't laid out as expected, no player cards");
            _failed = true;
            Drop();
            return false;
        }
        _textTurn = Quaternion.Inverse(Quaternion.Inverse(_card.rotation) * _ui.textLines[0].textLine.transform.rotation);
        LearnFont();
        _card.gameObject.SetActive(false);
        return true;
    }

    private void LearnFont()
    {
        if (Avatars.TagFont != null)
            return;
        try
        {
            var asset = _ui.textLines[0].textLine.text_?.font;
            Avatars.TagFont = asset != null ? asset.sourceFontFile : null;
            _log.LogInfo($"Card font {(asset != null ? asset.name : "none")}, usable for name tags: {Avatars.TagFont != null}");
        }
        catch (System.Exception e)
        {
            _log.LogWarning($"Reading the card's font failed: {e.Message}");
        }
    }

    private void Drop()
    {
        if (_card != null)
            Object.Destroy(_card.gameObject);
        _card = null;
        _spot = null;
        _target = null;
        _shownStats = null;
    }

    internal static Remote Aimed(IEnumerable<Remote> remotes)
    {
        var camera = Camera.main;
        if (camera == null)
            return null;
        var view = camera.transform;
        Remote best = null;
        var bestDot = AimDot;
        foreach (var remote in remotes)
        {
            if (remote.Car == null || !remote.Car.activeSelf)
                continue;
            var to = remote.Car.transform.position - view.position;
            if (to.magnitude > Range)
                continue;
            var dot = Vector3.Dot(view.forward, to.normalized);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = remote;
            }
        }
        return best;
    }

    private void Fill(Remote remote)
    {
        _shownStats = remote.Stats;
        _shownCar = remote.Car;
        var stats = remote.Stats ?? new PlayerStats();
        var origin = remote.Controller != null && remote.Controller.carLocal != null ? remote.Controller.carLocal.carOrigin : null;
        var lines = _ui.textLines;
        _ui.meetSpot_UIMode = meetSpot_3DUI.MeetSpot_UIMode.racer_info;
        lines[0].textLine.SetText(remote.Tag);
        lines[1].textLine.SetText(origin != null ? PlanetJem.Localization.Locale.GetCarModelName(origin, false) : "");
        lines[2].textLine.SetText(meetSpot.FormatRacerRepPower(stats.Rep, stats.Hp));
        lines[3].textLine.SetText(remote.Crew.Length > 0 ? remote.Crew : NoCrew);
        _ui.update_specData(stats.Hp, stats.Handling, stats.Braking, true, stats.SpecFloor);
    }

    private void Fade(bool show)
    {
        _alpha = Mathf.MoveTowards(_alpha, show ? 1f : 0f, FadePerSecond * Time.unscaledDeltaTime);
        _group.alpha = _alpha;
        var active = show || _alpha > 0f;
        if (_card.gameObject.activeSelf != active)
            _card.gameObject.SetActive(active);
    }
}
