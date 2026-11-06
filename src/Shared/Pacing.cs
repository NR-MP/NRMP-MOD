using System;
using System.Numerics;

namespace NightRunnersMP.Shared;

// full rate while moving, slow heartbeat once still for a bit. parked cars at a meet are most of the traffic otherwise. first movement goes out right away
public sealed class SendPacer
{
    public const long StillAfterMs = 1000;
    public const long HeartbeatMs = 500;
    private const float MoveMetres = 0.02f;
    private const float SameRotation = 0.999999f;
    private const int SameRpm = 150;
    private const int SameGas = 16;
    private const int SameSteer = 2;
    private const int SameWheelRpm = 40;

    private CarState _anchor;
    private bool _hasAnchor;
    private long _movedMs;
    private long _sentMs;

    public bool ShouldSend(in CarState state, long nowMs)
    {
        var moved = !_hasAnchor
            || state.Scene != _anchor.Scene
            || Vector3.DistanceSquared(state.Position, _anchor.Position) > MoveMetres * MoveMetres
            || Math.Abs(Quaternion.Dot(state.Rotation, _anchor.Rotation)) < SameRotation
            || Math.Abs(state.Rpm - _anchor.Rpm) > SameRpm
            || Math.Abs(state.Gas - _anchor.Gas) > SameGas
            || Math.Abs(state.Steer - _anchor.Steer) > SameSteer
            || Math.Abs(state.RearWheelRpm - _anchor.RearWheelRpm) > SameWheelRpm
            || state.Slip != _anchor.Slip
            || state.Smoke != _anchor.Smoke;
        if (moved)
        {
            _anchor = state;
            _hasAnchor = true;
            _movedMs = nowMs;
        }
        if (nowMs - _movedMs >= StillAfterMs && nowMs - _sentMs < HeartbeatMs)
            return false;
        _sentMs = nowMs;
        return true;
    }
}

// how often i need someone's car. way past name tag range a few a second looks the same, other scene is hidden so just enough to notice them arrive
public static class Relevance
{
    public const float FullMetres = 500f;
    public const float NearMetres = 2000f;
    public const int NearIntervalMs = 100;
    public const int FarIntervalMs = 500;
    public const int ElsewhereIntervalMs = 1000;

    public static int IntervalMs(in CarState car, in CarState viewer)
    {
        if (car.Scene != viewer.Scene)
            return ElsewhereIntervalMs;
        var distance = Vector3.DistanceSquared(car.Position, viewer.Position);
        if (distance < FullMetres * FullMetres)
            return 0;
        return distance < NearMetres * NearMetres ? NearIntervalMs : FarIntervalMs;
    }
}
