using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// sometimes you can't get into storage or a safehouse till you restart. the game hides every way in while one of
// these flags is on (enterLocation_UI.CanShowNow), so when one sticks while driving around it gets logged by name
internal sealed class EntryWatch
{
    private const float CheckSeconds = 1f;
    private const float StuckSeconds = 8f;

    private readonly ManualLogSource _log;
    private readonly List<string> _on = new();
    private float _nextCheck;
    private float _since;
    private string _last = "";
    private bool _told;

    public EntryWatch(ManualLogSource log) => _log = log;

    public void Tick(bool racing)
    {
        if (Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + CheckSeconds;
        var god = GodConstant.Instance;
        if (god == null || god.scene_currentType != GodConstant.Scene_currentType.FREEROAM || racing)
        {
            Clear();
            return;
        }

        _on.Clear();
        if (god.gameIsPaused) _on.Add("gameIsPaused");
        if (god.cinematic_playing) _on.Add("cinematic_playing");
        if (god.noEntryToLocationsOrPause) _on.Add("noEntryToLocationsOrPause");
        if (god.drivingScene_returningToGame) _on.Add("drivingScene_returningToGame");
        if (god.no_pauseGame) _on.Add("no_pauseGame");
        if (god.freezeAllVehicles) _on.Add("freezeAllVehicles");
        if (god.loading_StayInLoadingScreen) _on.Add("loading_StayInLoadingScreen");
        if (god.nightEnd) _on.Add("nightEnd");
        if (god.game_currentMode != GodConstant.Game_currentMode.null_type) _on.Add($"game_currentMode={god.game_currentMode}");
        var ui = god.UI_Data;
        if (ui != null)
        {
            if (ui.ui_showWarning) _on.Add("ui_showWarning");
            if (ui.loading_fadingToBlack) _on.Add("loading_fadingToBlack");
            if (ui.loading_screenIsBlack) _on.Add("loading_screenIsBlack");
        }
        if (_on.Count == 0)
        {
            if (_told)
                _log.LogInfo("Entries open again");
            Clear();
            return;
        }

        var now = string.Join(", ", _on);
        if (now != _last)
        {
            _last = now;
            _since = Time.unscaledTime;
            _told = false;
        }
        if (!_told && Time.unscaledTime - _since >= StuckSeconds)
        {
            _told = true;
            _log.LogWarning($"Storage and safehouse entries blocked for {StuckSeconds:F0}s by: {now}");
        }
    }

    private void Clear()
    {
        _last = "";
        _told = false;
    }
}
