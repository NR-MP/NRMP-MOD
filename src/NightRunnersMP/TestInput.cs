using System;
using PlanetJem.GameSettings;
using UnityEngine;

namespace NightRunnersMP;

// couch test from two-players.bat, two windows on one pc. both are plain keyboard and mouse, whichever one you clicked gets the keys.
// does nothing without NRMP_INPUT
internal sealed class TestInput
{
    private const float CheckSeconds = 0.5f;

    private readonly bool _on = Environment.GetEnvironmentVariable("NRMP_INPUT") != null;
    private float _nextCheck;

    public void Tick()
    {
        if (!_on || Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + CheckSeconds;
        // the game's own limiter, the window you're not in drops to the background cap otherwise. never saved so normal play keeps its settings
        if (FpsLimiter.UserMode != FpsMode.Fps60)
            FpsLimiter.SetUser(FpsMode.Fps60);
        if (FpsLimiter.BackgroundMode != FpsMode.Fps60)
            FpsLimiter.SetBackground(FpsMode.Fps60);
    }
}
