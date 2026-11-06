using System.Collections.Generic;
using BepInEx.Logging;
using PlanetJem.Races;
using PlanetJem.Roads.Locations;
using UnityEngine;

namespace NightRunnersMP;

// teleport = game's fast travel at 0 cost, fades, streams, sets you down. places come from the map registry so game updates just show up.
// custom map places have nothing to stream, just set the car down
internal sealed class Teleports
{
    public delegate bool Pose(out Vector3 position, out Quaternion rotation);

    public delegate bool Locate(Remote remote, out Vector3 position, out Quaternion rotation);

    internal sealed class Place
    {
        public string Name;
        public string Kind;
        public Pose Pose;
        public bool Direct;
        public System.Action Run;
    }

    private const float LeaveSeconds = 1.5f;
    private const float TimeoutSeconds = 20f;
    private const float TrackSeconds = 3f;
    private const float PlayerGap = 8f;

    private readonly ManualLogSource _log;
    private readonly System.Func<string> _leavePa;
    private readonly System.Action<string> _notice;

    private Place _going;
    private Place _tracked;
    private bool _left;
    private float _settleAt;
    private float _deadline;
    private float _trackUntil;
    private GameObject _target;

    public bool Busy => _going != null;

    public Teleports(ManualLogSource log, System.Func<string> leavePa, System.Action<string> notice)
    {
        _log = log;
        _leavePa = leavePa;
        _notice = notice;
    }

    public List<Place> Places()
    {
        var places = new List<Place>();
        if (!RoadLocationRegistry.TryGetInstance(out var registry) || registry == null)
            return places;
        foreach (var poi in registry.pointsOfInterest_)
        {
            if (poi == null || poi.Category == RoadLocationCategory.DestinationPoint)
                continue;
            var name = poi.DisplayName;
            places.Add(new Place
            {
                Name = string.IsNullOrEmpty(name) ? poi.PoiId : name,
                Kind = Kind(poi.Category),
                Pose = (out Vector3 position, out Quaternion rotation) => PoseOf(poi, out position, out rotation),
            });
        }
        places.Sort((a, b) => string.CompareOrdinal(a.Kind + a.Name, b.Kind + b.Name));
        return places;
    }

    public Place FirstPa() => Places().Find(p => p.Kind == "PA");

    // player in a PA is inside it, closest on the road is the entrance
    public Place ToPlayer(Remote remote, Locate locate, bool atPa, bool direct) => new()
    {
        Name = remote.Tag,
        Kind = "PLAYER",
        Direct = direct,
        Pose = (out Vector3 position, out Quaternion rotation) =>
        {
            if (!locate(remote, out position, out rotation))
                return false;
            if (atPa)
                return NearestPa(position, out position, out rotation);
            position -= rotation * Vector3.forward * PlayerGap;
            return true;
        },
    };

    public void Go(Place place)
    {
        if (_going != null)
            return;
        _going = place;
        _left = false;
        _settleAt = 0;
        _deadline = Time.unscaledTime + TimeoutSeconds;
        _log.LogInfo($"Teleporting to {place.Name}");
    }

    public void Tick(RCC_CarControllerV3 car)
    {
        KeepTarget();
        if (_going == null)
            return;
        var god = GodConstant.Instance;
        if (god == null)
            return;
        if (Time.unscaledTime >= _deadline)
        {
            Fail("The teleport timed out");
            return;
        }
        var type = god.scene_currentType;
        if (type == GodConstant.Scene_currentType.MEETSPOT)
        {
            if (!_left)
            {
                _left = true;
                _leavePa();
            }
            return;
        }
        if (type != GodConstant.Scene_currentType.FREEROAM)
        {
            Fail("Drive out first, then teleport");
            return;
        }
        if (_left && _settleAt == 0)
            _settleAt = Time.unscaledTime + LeaveSeconds;
        if (Time.unscaledTime < _settleAt || car == null)
            return;

        if (!_going.Pose(out var position, out var rotation))
        {
            Fail($"Can't find {_going.Name}");
            return;
        }
        if (_going.Direct)
            SetDown(car, position, rotation);
        else
            FastTravel(god, position, rotation);
        _going = null;
    }

    private void FastTravel(GodConstant god, Vector3 position, Quaternion rotation)
    {
        if (_target == null)
        {
            _target = new GameObject("NRMP teleport target");
            Object.DontDestroyOnLoad(_target);
        }
        _target.transform.SetPositionAndRotation(position, rotation);
        _tracked = _going;
        _trackUntil = Time.unscaledTime + TrackSeconds;
        var scene = god.floatingPoint_drivingScene;
        scene.StartCoroutine(scene.fastTravel_Basic(0, _target.transform));
    }

    // world can shift during the fade and fast travel reads the target late
    private void KeepTarget()
    {
        if (_tracked == null || _target == null)
            return;
        if (Time.unscaledTime >= _trackUntil)
            _tracked = null;
        else if (_tracked.Pose(out var position, out var rotation))
            _target.transform.SetPositionAndRotation(position, rotation);
    }

    private static void SetDown(RCC_CarControllerV3 car, Vector3 position, Quaternion rotation)
    {
        car.transform.SetPositionAndRotation(position, rotation);
        var body = car.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        RaceCarPhysics.SnapOntoSurface(car);
    }

    private void Fail(string reason)
    {
        _log.LogWarning($"Teleport to {_going.Name} failed: {reason}");
        _going = null;
        _notice(reason);
    }

    private static bool NearestPa(Vector3 from, out Vector3 position, out Quaternion rotation)
    {
        position = from;
        rotation = Quaternion.identity;
        if (!RoadLocationRegistry.TryGetInstance(out var registry) || registry == null)
            return false;
        var best = float.MaxValue;
        var found = false;
        foreach (var poi in registry.pointsOfInterest_)
        {
            if (poi == null || poi.Category != RoadLocationCategory.MeetSpot || !PoseOf(poi, out var at, out var turn))
                continue;
            var distance = (at - from).sqrMagnitude;
            if (distance >= best)
                continue;
            best = distance;
            position = at;
            rotation = turn;
            found = true;
        }
        return found;
    }

    private static bool PoseOf(RoadPointOfInterest poi, out Vector3 position, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (poi.TryGetPrimaryAnchor(out var anchor) && anchor.TryGetWorldPose(out position, out var tangent))
        {
            if (tangent.sqrMagnitude > 0)
                rotation = Quaternion.LookRotation(tangent);
            return true;
        }
        var target = poi.VisualTarget;
        position = target != null ? target.position : default;
        return target != null;
    }

    private static string Kind(RoadLocationCategory category) => category switch
    {
        RoadLocationCategory.MeetSpot => "PA",
        RoadLocationCategory.Garage => "GARAGE",
        RoadLocationCategory.CarDealer => "CAR DEALER",
        RoadLocationCategory.CarStorage => "CAR STORAGE",
        RoadLocationCategory.Shop => "SHOP",
        RoadLocationCategory.Tuning => "TUNING",
        RoadLocationCategory.GasStation => "GAS STATION",
        RoadLocationCategory.FastTravel => "TRUCK STATION",
        RoadLocationCategory.Challenge => "CHALLENGE",
        _ => "PLACE",
    };
}
