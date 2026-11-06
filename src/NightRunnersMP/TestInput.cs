using System;
using PlanetJem.GameSettings;
using PlanetJem.Input;
using Rewired;
using UnityEngine;

namespace NightRunnersMP;

// couch test from two-players.bat, one pc and two screens. window 1 never sees the pad. window 2 keeps the pad with no focus,
// and only takes keyboard and mouse while i've clicked into it for menus. does nothing without NRMP_INPUT
internal sealed class TestInput
{
    private const float CheckSeconds = 0.5f;

    private readonly string _mode = Environment.GetEnvironmentVariable("NRMP_INPUT");
    private InputRuntime _runtime;
    private bool _set;
    private float _nextCheck;

    // array's Prologue plugin turns this back on every Update. this runs after it, in time for the next frame's input
    public void LateTick()
    {
        if (_mode == "controller" && ReInput.isReady)
            ReInput.configuration.ignoreInputWhenAppNotInFocus = false;
    }

    public void Tick()
    {
        if (_mode == null || Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + CheckSeconds;
        // the game's own limiter, the window you're not in drops to the background cap otherwise. never saved so normal play keeps its settings
        if (FpsLimiter.UserMode != FpsMode.Fps60)
            FpsLimiter.SetUser(FpsMode.Fps60);
        if (FpsLimiter.BackgroundMode != FpsMode.Fps60)
            FpsLimiter.SetBackground(FpsMode.Fps60);
        if (_runtime == null)
            _runtime = UnityEngine.Object.FindObjectOfType<InputRuntime>();
        var player = _runtime != null && ReInput.isReady ? _runtime.LocalPlayer : null;
        if (player == null)
            return;

        var padWindow = _mode == "controller";
        if (!_set)
        {
            _set = true;
            if (!padWindow)
                player.controllers.excludeFromControllerAutoAssignment = true;
            NetBehaviour.Log.LogInfo($"Test input: {_mode} window, {player.controllers.joystickCount} pads on this player");
        }
        if (padWindow)
        {
            player.controllers.hasKeyboard = Application.isFocused;
            player.controllers.hasMouse = Application.isFocused;
        }
        else if (player.controllers.joystickCount > 0)
        {
            player.controllers.ClearControllersOfType(ControllerType.Joystick);
        }
    }
}
