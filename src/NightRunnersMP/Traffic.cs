using BepInEx.Configuration;
using BepInEx.Logging;
using PlanetJem.Roads.Traffic;
using UnityEngine;

namespace NightRunnersMP;

// traffic off on your screen. the game turns traffic back on itself after meetspots and loads, so this keeps checking
internal sealed class Traffic
{
    private const float CheckEvery = 1f;

    public static ConfigEntry<bool> Off;

    private readonly ManualLogSource _log;
    private float _checkAt;
    private bool _stopped;

    public Traffic(ManualLogSource log) => _log = log;

    public void Tick()
    {
        if (Time.unscaledTime < _checkAt)
            return;
        _checkAt = Time.unscaledTime + CheckEvery;
        var coordinator = TrafficCoordinator.Instance;
        if (coordinator == null)
            return;
        if (Off.Value && coordinator.IsTrafficEnabled)
        {
            coordinator.SetTrafficEnabled(false, true);
            if (!_stopped)
                _log.LogInfo("Traffic off");
            _stopped = true;
        }
        // only give it back if we were the ones holding it off, a meetspot keeps its own traffic off
        else if (!Off.Value && _stopped)
        {
            coordinator.SetTrafficEnabled(true, false);
            _stopped = false;
            _log.LogInfo("Traffic back on");
        }
    }

    public void Toggle()
    {
        Off.Value = !Off.Value;
        _checkAt = 0f;
    }
}
