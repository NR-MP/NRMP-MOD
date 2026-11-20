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
    public const double MaxLatencyMs = 200;
    private const double MaxRateChange = 0.05;
    private const double RateGainPerMs = 0.002;
    private const double MaxLeadRateChange = 0.01;
    private const double LeadRateGainPerMs = 0.0005;
    private const double ResyncMs = 500;
    private const double MaxTrackedGapMs = 100;
    private const double GapSmoothing = 0.1;
    private const long WindowMs = 2000;
    private const int OlderWindows = 8;
    private const float MaxSpinSeconds = 0.12f;
    private const float ErrorFadeSeconds = 0.15f;
    private const float PopMetres = 0.1f;
    private const float TeleportMetres = 30f;
    private const float PopRadians = 0.05f;
    private const float MaxTurnRadiansPerSecond = 6.3f;
    private const float FullLeadSeconds = 0.3f;
    private const float StopLeadSeconds = 0.6f;
    private const float ModelSeconds = 0.08f;
    private const float MaxAccel = 25f;
    private const float MaxSpin = 4f;
    private const float MaxLeadRadians = 0.5f;
    private const int Capacity = 32;

    private readonly CarState[] _buffer = new CarState[Capacity];
    private int _count;
    private int _revision;

    // transit times of the last two windows, relative to sender clock
    private long _windowEnd;
    private long _lowest = long.MaxValue;
    private long _highest = long.MinValue;
    private long _lowestBefore = long.MaxValue;
    private long _highestBefore = long.MinValue;
    private readonly long[] _lowestOlder = new long[OlderWindows];
    private double _gapMs = 1000.0 / Protocol.SendRateHz;

    private bool _playing;
    private bool _leading;
    private double _playMs;
    private double _lastNowMs;

    private bool _extrapolating;
    private bool _hasRaw;
    private double _rawMs;
    private int _rawRevision;
    private Vector3 _rawPosition;
    private Quaternion _rawRotation;
    private Vector3 _positionError;
    private Quaternion _rotationError = Quaternion.Identity;

    private Vector3 _accel;
    private Vector3 _spin;

    // session stats for the log: data ran out, it showed, took a resync
    public int RanOut { get; private set; }
    public int Pops { get; private set; }
    public int Resyncs { get; private set; }

    // no playback delay: newest state run forward to where the sender is right now
    public bool Predict;
    // sender to here. transit times only give clock difference plus this, so it has to come from pings
    public double LatencyMs;

    public bool HasData => _count > 0;
    public ushort Scene => _count > 0 ? _buffer[_count - 1].Scene : (ushort)0;
    public double LeadMs => _leading && _count > 0 ? _playMs - _buffer[_count - 1].TimeMs : 0;

    private double ClockOffset => Math.Min(_lowest, _lowestBefore);

    // a longer look than ClockOffset, at speed every ms the clock moves is seen
    private double SteadyOffset
    {
        get
        {
            var lowest = Math.Min(_lowest, _lowestBefore);
            foreach (var older in _lowestOlder)
                lowest = Math.Min(lowest, older);
            return lowest;
        }
    }

    public Interpolator() => Array.Fill(_lowestOlder, long.MaxValue);

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
            Model(_buffer[_count - 1], state);
        }
        if (_count == Capacity)
        {
            Array.Copy(_buffer, 1, _buffer, 0, Capacity - 1);
            _count--;
        }
        _buffer[_count++] = state;
        _revision++;
    }

    private void TrackTransit(long transit, long localNowMs)
    {
        if (localNowMs >= _windowEnd)
        {
            _windowEnd = localNowMs + WindowMs;
            Array.Copy(_lowestOlder, 0, _lowestOlder, 1, OlderWindows - 1);
            _lowestOlder[0] = _lowestBefore;
            _lowestBefore = _lowest;
            _highestBefore = _highest;
            _lowest = transit;
            _highest = transit;
            return;
        }
        _lowest = Math.Min(_lowest, transit);
        _highest = Math.Max(_highest, transit);
    }

    private void Model(in CarState before, in CarState state)
    {
        var gapMs = state.TimeMs - before.TimeMs;
        if (gapMs > MaxExtrapolateMs)
        {
            _accel = default;
            _spin = default;
            return;
        }
        var seconds = gapMs / 1000f;
        var blend = 1 - MathF.Exp(-seconds / ModelSeconds);

        var accel = (state.Velocity - before.Velocity) / seconds;
        // bumps aren't a trend
        accel.Y = 0;
        _accel += (Limit(accel, MaxAccel) - _accel) * blend;

        var turn = state.Rotation * Quaternion.Conjugate(before.Rotation);
        if (turn.W < 0)
            turn = Quaternion.Negate(turn);
        var axis = new Vector3(turn.X, turn.Y, turn.Z);
        var sine = axis.Length();
        var spin = sine > 1e-6f ? axis * (2 * MathF.Atan2(sine, turn.W) / sine / seconds) : default;
        _spin += (Limit(spin, MaxSpin) - _spin) * blend;
    }

    private static Vector3 Limit(Vector3 v, float max)
    {
        var length = v.Length();
        return length > max ? v * (max / length) : v;
    }

    // newest position as sent, no playback. sampling moves the playback on, this doesn't
    public bool TryLast(out Vector3 position)
    {
        position = _count > 0 ? _buffer[_count - 1].Position : default;
        return _count > 0;
    }

    public bool TrySample(double localNowMs, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        if (_count == 0)
            return false;

        var elapsedMs = _playing ? localNowMs - _lastNowMs : 0;
        Vector3 rawPosition;
        Quaternion rawRotation;
        if (Predict)
        {
            Lead(localNowMs, elapsedMs, out rawPosition, out rawRotation);
        }
        else
        {
            if (_leading)
            {
                _leading = false;
                _playMs = localNowMs - ClockOffset - TargetDelayMs - elapsedMs;
            }
            Advance(localNowMs, elapsedMs);
            Raw(_playMs, out rawPosition, out rawRotation, out var velocity);
            Smooth(rawPosition, rawRotation, velocity, (float)(elapsedMs / 1000));
        }
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

    private void Lead(double localNowMs, double elapsedMs, out Vector3 position, out Quaternion rotation)
    {
        var target = localNowMs - SteadyOffset + Math.Clamp(LatencyMs, 0, MaxLatencyMs);
        var jumped = !_leading || Math.Abs(target - _playMs) > ResyncMs;
        if (jumped)
        {
            if (_leading)
                Resyncs++;
            _playMs = target;
        }
        else
        {
            _playMs += elapsedMs;
            _playMs += elapsedMs * Math.Clamp((target - _playMs) * LeadRateGainPerMs, -MaxLeadRateChange, MaxLeadRateChange);
        }
        _playing = true;
        _leading = true;
        _lastNowMs = localNowMs;

        // not drawn for a while, nothing on screen to stay smooth with
        if (elapsedMs > ResyncMs)
            Settle();
        var fade = MathF.Exp(-(float)(elapsedMs / 1000) / ErrorFadeSeconds);
        _positionError *= fade;
        _rotationError = Quaternion.Slerp(Quaternion.Identity, _rotationError, fade);

        // a new state also changes where the car should have been last frame. that part fades in, the rest is real motion
        if (_hasRaw && (jumped || _revision != _rawRevision))
        {
            Ahead(jumped ? _playMs : _rawMs, out var was, out var faced);
            var pop = _rawPosition - was;
            if (pop.Length() > TeleportMetres)
            {
                Settle();
            }
            else
            {
                if (pop.Length() > PopMetres)
                    Pops++;
                _positionError += pop;
                _rotationError = Quaternion.Normalize(_rotationError * _rawRotation * Quaternion.Conjugate(faced));
            }
        }

        Ahead(_playMs, out position, out rotation);
        _rawMs = _playMs;
        _rawRevision = _revision;
        _rawPosition = position;
        _rawRotation = rotation;
        _hasRaw = true;

        ref var last = ref _buffer[_count - 1];
        var starved = _playMs - last.TimeMs > FullLeadSeconds * 1000 && last.Velocity.LengthSquared() > 1f;
        if (starved && !_extrapolating)
            RanOut++;
        _extrapolating = starved;
    }

    private void Settle()
    {
        _hasRaw = false;
        _positionError = default;
        _rotationError = Quaternion.Identity;
    }

    private void Ahead(double t, out Vector3 position, out Quaternion rotation)
    {
        ref var last = ref _buffer[_count - 1];
        if (t <= last.TimeMs)
        {
            Stored(t, out position, out rotation, out _);
            return;
        }

        // full speed for a while without news, then rolls to a stop instead of driving off through a wall
        var seconds = (float)((t - last.TimeMs) / 1000);
        if (seconds > FullLeadSeconds)
        {
            var over = Math.Min(seconds, StopLeadSeconds) - FullLeadSeconds;
            seconds = FullLeadSeconds + over - over * over / (2 * (StopLeadSeconds - FullLeadSeconds));
        }

        // braking ends at standstill, it doesn't reverse
        var slowing = -Vector3.Dot(last.Velocity, _accel);
        var moving = slowing > 0 ? Math.Min(seconds, last.Velocity.LengthSquared() / slowing) : seconds;
        position = last.Position + last.Velocity * moving + _accel * (0.5f * moving * moving);

        var turn = _spin * seconds;
        var angle = turn.Length();
        rotation = angle > 1e-5f
            ? Quaternion.Normalize(Quaternion.CreateFromAxisAngle(turn / angle, Math.Min(angle, MaxLeadRadians)) * last.Rotation)
            : last.Rotation;
    }

    private void Raw(double t, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
    {
        var running = t > _buffer[_count - 1].TimeMs;
        if (running && !_extrapolating)
            RanOut++;
        _extrapolating = running;

        if (!Stored(t, out position, out rotation, out velocity))
            Extrapolate(t, out position, out rotation, out velocity);
    }

    private bool Stored(double t, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
    {
        if (t <= _buffer[0].TimeMs)
        {
            position = _buffer[0].Position;
            rotation = _buffer[0].Rotation;
            velocity = default;
            return true;
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
            return true;
        }

        position = default;
        rotation = Quaternion.Identity;
        velocity = default;
        return false;
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
