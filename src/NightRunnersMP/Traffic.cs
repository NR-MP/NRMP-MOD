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
    private bool _following;

    public Traffic(ManualLogSource log) => _log = log;

    public bool Following
    {
        get => _following;
        set
        {
            if (_following == value)
                return;
            _following = value;
            _checkAt = 0f;
        }
    }

    public void Tick()
    {
        if (Time.unscaledTime < _checkAt)
            return;
        _checkAt = Time.unscaledTime + CheckEvery;
        var coordinator = TrafficCoordinator.Instance;
        if (coordinator == null)
            return;
        var off = Off.Value || _following;
        if (off && coordinator.IsTrafficEnabled)
        {
            coordinator.SetTrafficEnabled(false, true);
            if (!_stopped)
                _log.LogInfo(_following ? "Traffic off, showing the shared traffic" : "Traffic off");
            _stopped = true;
        }
        // only give it back if we were the ones holding it off, a meetspot keeps its own traffic off
        else if (!off && _stopped)
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
