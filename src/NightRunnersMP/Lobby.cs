using System;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightRunnersMP;

internal sealed class Lobby
{
    private const float ScanSeconds = 1f;
    private const int DumpDepth = 7;
    private const int DumpLines = 300;

    // intro video starts menu music and camera just past here
    private const double IntroVideoSeekSeconds = 10.01;

    private readonly ManualLogSource _log;
    private readonly Action<bool> _setOnline;
    private readonly bool _skipIntro;

    private mainMenu _intro;
    private bool _introSeeked;
    private mainMenu.MainMenu_mode _lastMode = mainMenu.MainMenu_mode.null_mode;
    private bool _showing;
    private bool _seenCar;
    private bool _dumped;
    private float _nextScan;
    private GodConstant.Scene_currentType _lastSceneType;

    public Lobby(ManualLogSource log, Action<bool> setOnline, bool skipIntro)
    {
        _log = log;
        _setOnline = setOnline;
        _skipIntro = skipIntro;
    }

    public void Tick(RCC_CarControllerV3 car)
    {
        if (_intro != null)
            SkipIntro();

        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + ScanSeconds;

        ScanMenu();
        LogPlace(car);
        if (car != null)
            _seenCar = true;
    }

    // game has its own skip, but only once the video started music + camera. any sooner the menu has neither, so seek there then skip
    private void SkipIntro()
    {
        if (_intro.introSkipHintTime_ >= 0f || _intro.introSkippable_)
        {
            _intro.introSkipped_ = true;
            _intro = null;
            _log.LogInfo("Intro skipped");
            return;
        }
        var video = _intro.intro_videoPlayer;
        if (!_introSeeked && video != null && video.isPrepared)
        {
            _introSeeked = true;
            video.time = IntroVideoSeekSeconds;
        }
    }

    private void ScanMenu()
    {
        var menu = UnityEngine.Object.FindObjectOfType<mainMenu>();
        if (menu != null && menu.mainMenu_mode != _lastMode)
        {
            _lastMode = menu.mainMenu_mode;
            _log.LogInfo($"Main menu mode {_lastMode}, started {menu.startedGame}");
        }

        if (_skipIntro && _intro == null && menu != null && menu.mainMenu_mode == mainMenu.MainMenu_mode.intro && !menu.introSkipped_)
        {
            _intro = menu;
            _introSeeked = false;
        }

        var showing = menu != null && menu.mainMenu_mode == mainMenu.MainMenu_mode.mainMenu && !menu.startedGame;
        if (showing && !_showing)
        {
            if (_seenCar)
            {
                _seenCar = false;
                _setOnline(false);
            }
            if (!_dumped)
            {
                _dumped = true;
                DumpMenu();
            }
        }
        _showing = showing;
    }

    // can't read from the build how the car sits in a parking stop either
    private void LogPlace(RCC_CarControllerV3 car)
    {
        var god = GodConstant.Instance;
        if (god == null || god.scene_currentType == _lastSceneType)
            return;
        _lastSceneType = god.scene_currentType;

        var spot = god.currentmeetspot;
        _log.LogInfo($"Now in {_lastSceneType}, active scene {SceneManager.GetActiveScene().name}, stop {god.player_currentMeetspot}");
        _log.LogInfo($"Car {(car != null ? $"at {car.transform.position}, active {car.gameObject.activeInHierarchy}" : "none")}");
        if (spot == null)
            return;
        var parked = spot.parking_playerCar;
        var mine = spot.player_parkingSpot;
        _log.LogInfo($"Stop {spot.meetSpot_ID}: {spot.meetSpot_ParkingSpaces?.Count ?? 0} spaces, " +
            $"own space {(mine != null ? mine.position.ToString() : "none")}, parked car {(parked != null ? parked.transform.position.ToString() : "none")}, " +
            $"on foot {(spot.localController_ != null ? spot.localController_.transform.position.ToString() : "no")}");
    }

    // can't read method bodies from the build, so get the layout from the running game
    private void DumpMenu()
    {
        var text = new StringBuilder("Main menu UI:\n");
        var lines = 0;
        foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            if (canvas.transform.parent == null || canvas.isRootCanvas)
                Dump(canvas.transform, 0, text, ref lines);
        _log.LogInfo(text.ToString());
    }

    private static void Dump(Transform t, int depth, StringBuilder text, ref int lines)
    {
        if (depth > DumpDepth || lines >= DumpLines || !t.gameObject.activeInHierarchy)
            return;
        lines++;
        var kind = t.GetComponent<buttonDataHolder>() != null ? " [menu button]"
            : t.GetComponent<Button>() != null ? " [button]"
            : t.GetComponent<scrollParent>() != null ? " [scroll]" : "";
        text.Append(' ', depth * 2).Append(t.name).Append(kind).Append('\n');
        for (var i = 0; i < t.childCount; i++)
            Dump(t.GetChild(i), depth + 1, text, ref lines);
    }
}
