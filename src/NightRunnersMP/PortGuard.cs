using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// array's trip back to Ikaho can hang on the loading screen or drop you under the road. this watches it and
// fast travels you to an Ikaho PA when it goes wrong
internal sealed class PortGuard
{
    private const float HangSeconds = 60f;
    private const float WatchSeconds = 15f;
    private const float BadSeconds = 1.5f;
    private const float FallSpeed = 30f;
    private const float GroundRay = 300f;

    private readonly ManualLogSource _log;
    private readonly MapPort _port;
    private readonly Teleports _teleports;
    private readonly System.Action<string> _notice;
    private float _returningSince;
    private float _watchUntil;
    private float _badSince;
    private bool _wasActive;

    public PortGuard(ManualLogSource log, MapPort port, Teleports teleports, System.Action<string> notice)
    {
        _log = log;
        _port = port;
        _teleports = teleports;
        _notice = notice;
    }

    public void Tick(RCC_CarControllerV3 car)
    {
        var now = Time.unscaledTime;
        if (!_port.Returning)
            _returningSince = 0;
        else if (_returningSince == 0)
            _returningSince = now;
        else if (now - _returningSince >= HangSeconds)
        {
            _returningSince = 0;
            _port.Abandon();
            Rescue("hung for a minute");
        }

        // landed back, give it a few seconds to show it's on the road
        var active = _port.Active;
        if (_wasActive && !active)
        {
            _watchUntil = now + WatchSeconds;
            _badSince = 0;
        }
        _wasActive = active;
        if (_watchUntil == 0)
            return;
        if (now >= _watchUntil)
        {
            _watchUntil = 0;
            return;
        }
        if (car == null || _teleports.Busy || GodConstant.Instance?.scene_currentType != GodConstant.Scene_currentType.FREEROAM || Fine(car))
        {
            _badSince = 0;
            return;
        }
        if (_badSince == 0)
            _badSince = now;
        else if (now - _badSince >= BadSeconds)
        {
            _watchUntil = 0;
            Rescue("left the car under the road");
        }
    }

    // road under it and not dropping
    private static bool Fine(RCC_CarControllerV3 car)
    {
        var body = car.GetComponent<Rigidbody>();
        if (body != null && body.velocity.y < -FallSpeed)
            return false;
        var from = car.transform.position + Vector3.up * 2f;
        foreach (var hit in Physics.RaycastAll(from, Vector3.down, GroundRay, ~0, QueryTriggerInteraction.Ignore))
            if (hit.rigidbody == null || hit.rigidbody != body)
                return true;
        return false;
    }

    private void Rescue(string why)
    {
        var pa = _teleports.FirstPa();
        _log.LogWarning($"Prologue return {why}, {(pa != null ? $"sending you to {pa.Name}" : "no PA to send you to")}");
        if (pa == null)
        {
            _notice("The trip back from C1 went wrong. Teleport out with F7");
            return;
        }
        _notice($"The trip back from C1 went wrong, sending you to {pa.Name}");
        _teleports.Go(pa);
    }
}
