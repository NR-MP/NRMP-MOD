using System;
using System.Text;
using BepInEx.Logging;
using PlanetJem.UI;
using PlanetJem.UI.Hints;
using PlanetJem.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightRunnersMP;

// multiplayer is a main menu choice, starts inside a parking stop the way a save made there loads. back to menu = offline.
// name popup is its own thing, the game's field renames the career
internal sealed class Lobby
{
    private const float ScanSeconds = 1f;
    private const int DumpDepth = 7;
    private const int DumpLines = 300;
    private const string ButtonLabel = "MULTIPLAYER";
    private const string ButtonKey = "M";

    // intro video starts menu music and camera just past here
    private const double IntroVideoSeekSeconds = 10.01;

    private readonly ManualLogSource _log;
    private readonly Func<bool> _readSteam;
    private readonly Action<bool> _setOnline;
    private readonly GodConstant.MeetSpots_All _stop;
    private readonly bool _skipIntro;

    private mainMenu _menu;
    private mainMenu _intro;
    private bool _introSeeked;
    private mainMenu.MainMenu_mode _lastMode = mainMenu.MainMenu_mode.null_mode;
    private bool _showing;
    private GameObject _prompt;
    private bool _promptFailed;
    private bool _noSteam;
    private bool _chosen;
    private bool _seenCar;
    private bool _dumped;
    private float _nextScan;
    private GodConstant.Scene_currentType _lastSceneType;
    private bool _swapSkyOnBlack;

    public Lobby(ManualLogSource log, Func<bool> readSteam, Action<bool> setOnline, GodConstant.MeetSpots_All stop, bool skipIntro)
    {
        _log = log;
        _readSteam = readSteam;
        _setOnline = setOnline;
        _stop = stop;
        _skipIntro = skipIntro;
    }

    public void Tick(RCC_CarControllerV3 car)
    {
        if (_swapSkyOnBlack)
            SwapSkyOnBlack();
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

    public void DrawMenu()
    {
        if (!_showing || _chosen)
            return;
        var e = Event.current;
        if (_noSteam)
            GUI.Label(new Rect(Screen.width / 2 - 130, Screen.height / 2, 260, 24), "OPEN STEAM TO PLAY MULTIPLAYER");
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.M)
        {
            _noSteam = !_readSteam();
            if (!_noSteam)
                StartMultiplayer();
        }
        else if (_prompt == null)
            GUI.Label(new Rect(Screen.width - 240, Screen.height - 80, 220, 24), $"[{ButtonKey}] {ButtonLabel}");
    }

    // menu keeps putting its own row back in the hint display so added prompts don't stick. copy of one of the row's items instead,
    // right before the start prompt. just a picture, the key gets read in DrawMenu
    private void AddPrompt()
    {
        if (_prompt != null)
            return;
        UIHintItemView last = null;
        foreach (var item in UnityEngine.Object.FindObjectsOfType<UIHintItemView>())
            if (last == null || item.transform.GetSiblingIndex() > last.transform.GetSiblingIndex())
                last = item;
        if (last == null)
            return;

        _prompt = UnityEngine.Object.Instantiate(last.gameObject, last.transform.parent);
        _prompt.name = "NRMP prompt";
        _prompt.transform.SetSiblingIndex(last.transform.GetSiblingIndex());

        var view = _prompt.GetComponent<UIHintItemView>();
        var slots = view.bindMulti_.slots_;
        slots[0].SetBind(ButtonKey, null);
        for (var i = 1; i < slots.Length; i++)
            slots[i].Clear();
        view.labelText_.SetText(ButtonLabel);
        view.ApplyRootWidth(view.MeasureBindWidth(), view.MeasureLabelWidth(ButtonLabel));
        _log.LogInfo("Menu prompt added");
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

    private void RemovePrompt()
    {
        if (_prompt != null)
            UnityEngine.Object.Destroy(_prompt);
        _prompt = null;
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
            _menu = menu;
            _chosen = false;
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
        if (!showing || _chosen)
        {
            RemovePrompt();
        }
        else if (!_promptFailed)
        {
            try
            {
                AddPrompt();
            }
            catch (Exception e)
            {
                _promptFailed = true;
                RemovePrompt();
                _log.LogError(e);
            }
        }
    }

    // menu start always loads the saved garage and a garage never reads the parking stop start. so same steps but the loading
    // screen gets the map, its setup starts at loading_startInMeetspot. no intro done yet = still has to go through the menu start for the new career
    private void StartMultiplayer()
    {
        _chosen = true;
        _setOnline(true);
        var god = GodConstant.Instance;
        if (god.noPlayerData || !god.player_hasCompletedIntro)
        {
            _log.LogInfo($"Multiplayer chosen, no finished save so starting in the garage");
            _menu.StartCoroutine(_menu.startGame());
            return;
        }

        _log.LogInfo($"Multiplayer chosen, parking stop {_stop}");
        UISounds.PlayFinalConfirm(true);
        _menu.hints_.Dispose();
        _menu.playerName_inputField.enabled = false;
        _menu.mainMenu_mode = mainMenu.MainMenu_mode.null_mode;
        god.loading_startInMeetspot = _stop;
        god.UI_Data.StartLoadingScreen1(SceneId.MountHaruna, 0f, false);
        _swapSkyOnBlack = true;
    }

    private void SwapSkyOnBlack()
    {
        var god = GodConstant.Instance;
        if (god == null || god.UI_Data.loading_fadingToBlack)
            return;
        _swapSkyOnBlack = false;
        god.skyManager.gameObject.SetActive(true);
        _menu.mainMenu_skyManager.gameObject.SetActive(false);
        god.reset_timeOfDay();
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
