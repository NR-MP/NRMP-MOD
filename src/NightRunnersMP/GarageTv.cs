using System;
using BepInEx.Logging;
using PlanetJem.Input;
using PlanetJem.UI;
using UnityEngine;

namespace NightRunnersMP;

internal sealed class GarageTv
{
    private const float FindSeconds = 1f;
    private const float AskSeconds = 5f;
    private const float StaleSeconds = 15f;
    private const string Title = "NRMP MOD";
    private const string Enter = "ENTER MULTIPLAYER";
    private const string Leave = "LEAVE MULTIPLAYER";
    private const int RewriteFrames = 10;

    private readonly ManualLogSource _log;
    private readonly Func<bool> _readSteam;
    private readonly Func<bool> _isOnline;
    private readonly Action<bool> _setOnline;
    private readonly Action _ask;

    private homegarageSpot _spot;
    private float _nextFind;
    private float _nextAsk;
    private int _online = -1;
    private int _max = Shared.Protocol.MaxPlayers;
    private float _heardAt;
    private bool _noSteam;
    private bool _failed;
    private int _lookFrames;
    private string _writtenMain;
    private string _writtenSub;

    public GarageTv(ManualLogSource log, Func<bool> readSteam, Func<bool> isOnline, Action<bool> setOnline, Action ask)
    {
        _log = log;
        _readSteam = readSteam;
        _isOnline = isOnline;
        _setOnline = setOnline;
        _ask = ask;
    }

    public void Heard(int online, int max)
    {
        _online = online;
        _max = max;
        _heardAt = Time.unscaledTime;
    }

    public void Tick()
    {
        var god = GodConstant.Instance;
        if (god == null || god.scene_currentType != GodConstant.Scene_currentType.GARAGE)
        {
            _spot = null;
            _noSteam = false;
            return;
        }
        if (_failed)
            return;
        try
        {
            Look(god);
        }
        catch (Exception e)
        {
            _failed = true;
            _log.LogError($"Garage tv: {e}");
        }
    }

    private void Look(GodConstant god)
    {
        var now = Time.unscaledTime;
        if (_spot == null)
        {
            if (now < _nextFind)
                return;
            _nextFind = now + FindSeconds;
            _spot = UnityEngine.Object.FindObjectOfType<homegarageSpot>();
            if (_spot == null)
                return;
        }
        if (now >= _nextAsk)
        {
            _nextAsk = now + AskSeconds;
            _ask();
        }

        var tv = _spot.multiplayer_collider;
        var ui = god.UI_Data;
        if (tv == null || ui == null || ui.customization_UI_root == null || !ui.customization_UI_root.activeSelf || _spot.raycast_customization() != tv)
        {
            _lookFrames = 0;
            return;
        }

        var online = _isOnline();
        var rewrite = _lookFrames < RewriteFrames;
        _lookFrames++;
        var main = online ? Leave : Enter;
        if (ui.customization_UI != null && (rewrite || main != _writtenMain))
        {
            ui.customization_UI.SetText(main);
            _writtenMain = main;
        }
        var sub = Sub(now);
        if (ui.customization_UI_subText != null && (rewrite || sub != _writtenSub))
        {
            ui.customization_UI_subText.SetText(sub);
            _writtenSub = sub;
        }

        if (GameInput.Released(InputActionId.WalkConfirm))
            Switch(online);
    }

    private string Sub(float now)
    {
        if (_noSteam)
            return "OPEN STEAM TO PLAY MULTIPLAYER";
        var count = _online >= 0 && now - _heardAt < StaleSeconds ? _online.ToString() : "?";
        return $"{Title} - {count}/{_max}";
    }

    private void Switch(bool online)
    {
        if (!online)
        {
            _noSteam = !_readSteam();
            if (_noSteam)
                return;
        }
        _log.LogInfo(online ? "Leaving multiplayer from the garage tv" : "Multiplayer from the garage tv");
        _setOnline(!online);
        UISounds.PlayFinalConfirm(true);
        _nextAsk = Time.unscaledTime + 1f;
    }
}
