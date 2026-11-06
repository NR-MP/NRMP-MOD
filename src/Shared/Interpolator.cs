using System;
using System.Numerics;

namespace NightRunnersMP.Shared;

// plays a remote car back slightly in the past. delay follows the connection, playback clock only ever nudges so it never jumps,
// states joined by curves using their velocities. unavoidable snap after data ran out gets faded instead of shown
public sealed class Interpolator
{
    public const double MinDelayMs = 70;
    public const double MaxDelayMs = 300;
    public const uint MaxExtrapolateMs = 250;
    private const double MaxRateChange = 0.05;
    private const double RateGainPerMs = 0.002;
    private const double ResyncMs = 500;
    private const double MaxTrackedGapMs = 100;
    private const double GapSmoothing = 0.1;
    private const long WindowMs = 2000;
    private const float MaxSpinSeconds = 0.12f;
    private const float ErrorFadeSeconds = 0.15f;
    private const float PopMetres = 0.1f;
    private const float TeleportMetres = 30f;
    private const float PopRadians = 0.05f;
    private const float MaxTurnRadiansPerSecond = 6.3f;
    private const int Capacity = 32;

    private readonly CarState[] _buffer = new CarState[Capacity];
    private int _count;

    // transit times of the last two windows, relative to sender clock
    private long _windowEnd;
    private long _lowest = long.MaxValue;
    private long _highest = long.MinValue;
    private long _lowestBefore = long.MaxValue;
    private long _highestBefore = long.MinValue;
    private double _gapMs = 1000.0 / Protocol.SendRateHz;

    private bool _playing;
    private double _playMs;
    private double _lastNowMs;

    private bool _extrapolating;
    private bool _hasRaw;
    private Vector3 _rawPosition;
    private Quaternion _rawRotation;
    private Vector3 _positionError;
    private Quaternion _rotationError = Quaternion.Identity;

    // session stats for the log: data ran out, it showed, took a resync
    public int RanOut { get; private set; }
    public int Pops { get; private set; }
    public int Resyncs { get; private set; }

    public bool HasData => _count > 0;
    public ushort Scene => _count > 0 ? _buffer[_count - 1].Scene : (ushort)0;

    private double ClockOffset => Math.Min(_lowest, _lowestBefore);

    // one lost packet + worst delivery of the last few seconds
    public double TargetDelayMs
    {
        get
        {
            var spread = Math.Max(_highest, _highestBefore) - ClockOffset;
            return Math.Clamp(2 * _gapMs + spread, MinDelayMs, MaxDelayMs);
        }
    }

    public void Push(CarState state, long localNowMs)
    {
        TrackTransit(localNowMs - state.TimeMs, localNowMs);
        if (_count > 0 && state.TimeMs <= _buffer[_count - 1].TimeMs)
            return;
        if (_count > 0)
        {
            var gap = Math.Min(state.TimeMs - _buffer[_count - 1].TimeMs, MaxTrackedGapMs);
            _gapMs += (gap - _gapMs) * GapSmoothing;
        }
        if (_count == Capacity)
        {
            Array.Copy(_buffer, 1, _buffer, 0, Capacity - 1);
            _count--;
        }
        _buffer[_count++] = state;
    }

    private void TrackTransit(long transit, long localNowMs)
    {
        if (localNowMs >= _windowEnd)
        {
            _windowEnd = localNowMs + WindowMs;
            _lowestBefore = _lowest;
            _highestBefore = _highest;
            _lowest = transit;
            _highest = transit;
            return;
        }
        _lowest = Math.Min(_lowest, transit);
        _highest = Math.Max(_highest, transit);
    }

    public bool TrySample(double localNowMs, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        if (_count == 0)
            return false;

        var elapsedMs = _playing ? localNowMs - _lastNowMs : 0;
        Advance(localNowMs, elapsedMs);
        Raw(_playMs, out var rawPosition, out var rawRotation, out var velocity);
        Smooth(rawPosition, rawRotation, velocity, (float)(elapsedMs / 1000));
        position = rawPosition + _positionError;
        rotation = Quaternion.Normalize(_rotationError * rawRotation);
        return true;
    }

    private void Advance(double localNowMs, double elapsedMs)
    {
        var target = localNowMs - ClockOffset - TargetDelayMs;
        if (!_playing || Math.Abs(target - _playMs) > ResyncMs)
        {
            if (_playing)
                Resyncs++;
            _playMs = target;
            _playing = true;
            _hasRaw = false;
            _positionError = default;
            _rotationError = Quaternion.Identity;
        }
        else
        {
            var rate = 1 + Math.Clamp((target - _playMs) * RateGainPerMs, -MaxRateChange, MaxRateChange);
            _playMs += elapsedMs * rate;
        }
        _lastNowMs = localNowMs;
    }

    private void Raw(double t, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
    {
        var running = t > _buffer[_count - 1].TimeMs;
        if (running && !_extrapolating)
            RanOut++;
        _extrapolating = running;

        if (t <= _buffer[0].TimeMs)
        {
            position = _buffer[0].Position;
            rotation = _buffer[0].Rotation;
            velocity = default;
            return;
        }

        for (var i = _count - 1; i > 0; i--)
        {
            ref var a = ref _buffer[i - 1];
            ref var b = ref _buffer[i];
            if (t < a.TimeMs)
                continue;
            if (t > b.TimeMs)
                break;
            var f = (float)((t - a.TimeMs) / (b.TimeMs - a.TimeMs));
            position = Curve(a, b, f);
            rotation = Quaternion.Slerp(a.Rotation, b.Rotation, f);
            velocity = Vector3.Lerp(a.Velocity, b.Velocity, f);
            return;
        }

        Extrapolate(t, out position, out rotation, out velocity);
    }

    // velocities bend the path through corners. jumps they can't explain (reset to road) stay straight
    private static Vector3 Curve(in CarState a, in CarState b, float f)
    {
        var seconds = (b.TimeMs - a.TimeMs) / 1000f;
        var reach = Math.Max(a.Velocity.Length(), b.Velocity.Length()) * seconds * 1.5f + 1f;
        if (Vector3.Distance(a.Position, b.Position) > reach)
            return Vector3.Lerp(a.Position, b.Position, f);

        var f2 = f * f;
        var f3 = f2 * f;
        return a.Position * (2 * f3 - 3 * f2 + 1)
            + a.Velocity * (seconds * (f3 - 2 * f2 + f))
            + b.Position * (-2 * f3 + 3 * f2)
            + b.Velocity * (seconds * (f3 - f2));
    }

    private void Extrapolate(double t, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
    {
        ref var last = ref _buffer[_count - 1];
        var aheadMs = t - last.TimeMs;
        var ahead = (float)(Math.Min(aheadMs, MaxExtrapolateMs) / 1000);
        position = last.Position + last.Velocity * ahead;
        velocity = aheadMs < MaxExtrapolateMs ? last.Velocity : default;
        rotation = last.Rotation;
        if (_count < 2)
            return;

        ref var previous = ref _buffer[_count - 2];
        var gap = (last.TimeMs - previous.TimeMs) / 1000f;
        var turn = last.Rotation * Quaternion.Conjugate(previous.Rotation);
        rotation = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, turn, Math.Min(ahead, MaxSpinSeconds) / gap) * last.Rotation);
    }

    // whatever the car's motion doesn't explain joins a fading error, except real teleports
    private void Smooth(Vector3 position, Quaternion rotation, Vector3 velocity, float seconds)
    {
        var fade = MathF.Exp(-seconds / ErrorFadeSeconds);
        _positionError *= fade;
        _rotationError = Quaternion.Slerp(Quaternion.Identity, _rotationError, fade);

        if (_hasRaw)
        {
            var expected = _rawPosition + velocity * seconds;
            var pop = expected - position;
            if (Vector3.Distance(position, _rawPosition) > TeleportMetres)
            {
                _positionError = default;
                _rotationError = Quaternion.Identity;
            }
            else
            {
                if (pop.Length() > PopMetres)
                {
                    _positionError += pop;
                    Pops++;
                }
                if (Angle(_rawRotation, rotation) > PopRadians + MaxTurnRadiansPerSecond * seconds)
                    _rotationError = Quaternion.Normalize(_rotationError * _rawRotation * Quaternion.Conjugate(rotation));
            }
        }
        _rawPosition = position;
        _rawRotation = rotation;
        _hasRaw = true;
    }

    public void ResetCounts()
    {
        RanOut = 0;
        Pops = 0;
        Resyncs = 0;
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Min(Math.Abs(Quaternion.Dot(a, b)), 1f));
}
