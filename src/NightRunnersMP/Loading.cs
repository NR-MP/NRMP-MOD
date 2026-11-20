using System;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using PlanetJem.Scenes;
using UnityEngine;

namespace NightRunnersMP;

internal sealed class Loading
{
    private const float OpenTimeout = 10f;
    private const float CloseTimeout = 15f;

    private static Loading _current;

    private readonly ManualLogSource _log;
    private GodConstant _god;
    private RCC_DashboardInputs _ui;
    private GameObject _car;
    private bool _opened;
    private bool _released;
    private bool _forwarded;
    private bool _finalFade;
    private bool _unload;
    private bool _noPause;
    private bool _freeze;
    private float _startedAt;
    private float _releasedAt;

    public bool Active { get; private set; }

    public Loading(ManualLogSource log) => _log = log;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GodConstant), nameof(GodConstant.LoadGameSceneAsync)), new HarmonyMethod(typeof(Loading), nameof(BeforeSceneLoad)));
        harmony.Patch(AccessTools.Method(typeof(GodConstant), nameof(GodConstant.LoadingScreen_Stop)), new HarmonyMethod(typeof(Loading), nameof(AllowStop)));
        harmony.Patch(AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) }), new HarmonyMethod(typeof(Loading), nameof(AllowDestroy)));
    }

    public bool Begin(GameObject car)
    {
        var god = GodConstant.Instance;
        var ui = god != null ? god.UI_Data : null;
        if (god == null || ui == null)
            return false;
        _god = god;
        _ui = ui;
        _car = car;
        _opened = _released = _forwarded = _finalFade = _unload = false;
        _noPause = god.no_pauseGame;
        _freeze = god.freezeAllVehicles;
        god.no_pauseGame = true;
        god.freezeAllVehicles = true;
        Active = true;
        _current = this;
        _startedAt = Time.unscaledTime;
        // Maps have no SceneId, otherwise BeforeSceneLoad swallows the load
        god.loading_sceneToLoad = SceneId.None;
        try
        {
            ui.StartLoadingScreen1(SceneId.None, 0f, false);
        }
        catch (Exception e)
        {
            _log.LogWarning($"Loading screen didn't start: {e.Message}");
        }
        return true;
    }

    public bool Ready
    {
        get
        {
            if (!Active)
                return true;
            if (!_opened && Time.unscaledTime - _startedAt >= OpenTimeout)
            {
                _opened = true;
                _log.LogWarning("Loading screen never asked for its scene, going on anyway");
            }
            return _opened && !(_ui != null && _ui.loading_fadingToBlack);
        }
    }

    public void Release()
    {
        if (!Active || _released)
            return;
        _released = true;
        _releasedAt = Time.unscaledTime;
        if (_god != null)
            _god.freezeAllVehicles = _freeze;
        _god?.LoadingScreen_Stop(true, false);
    }

    public bool Closed()
    {
        if (!Active)
            return true;
        if (!_released)
            return false;
        var over = _ui == null || _god == null || (_ui.loading_loadingFullyOver && _god.loading_animationFadeIsFinished);
        if (!over && Time.unscaledTime - _releasedAt < CloseTimeout)
            return false;
        if (!over)
            _log.LogWarning("Loading screen didn't report its fade as done, closing anyway");
        End();
        return true;
    }

    public void End()
    {
        if (!Active)
            return;
        Active = false;
        if (_current == this)
            _current = null;
        if (_god != null)
        {
            _god.no_pauseGame = _noPause;
            _god.freezeAllVehicles = _freeze;
            if (!_forwarded)
                _god.LoadingScreen_Stop(true, false);
        }
        _god = null;
        _ui = null;
        _car = null;
    }

    private static bool BeforeSceneLoad(GodConstant __instance, SceneId sceneId, ref UniTask __result)
    {
        var gate = _current;
        if (gate == null || !gate.Active || gate._god != __instance || sceneId != SceneId.None || gate._opened)
            return true;
        gate._opened = true;
        __result = UniTask.CompletedTask;
        return false;
    }

    private static bool AllowStop(GodConstant __instance, ref bool useFinalFadeFromBlack, ref bool unloadCustomization)
    {
        var gate = _current;
        if (gate == null || !gate.Active || gate._god != __instance)
            return true;
        gate._finalFade |= useFinalFadeFromBlack;
        gate._unload |= unloadCustomization;
        if (!gate._released || gate._forwarded)
            return false;
        gate._forwarded = true;
        useFinalFadeFromBlack = gate._finalFade;
        unloadCustomization = gate._unload;
        return true;
    }

    private static bool AllowDestroy(UnityEngine.Object __0)
    {
        var gate = _current;
        if (gate == null || !gate.Active || gate._opened || gate._car == null || __0 == null)
            return true;
        return __0 != gate._car;
    }
}
