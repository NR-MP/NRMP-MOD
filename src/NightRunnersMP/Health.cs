using System.Diagnostics;
using UnityEngine;

namespace NightRunnersMP;

// adds up mod cost per frame, for a log line on how a session went
internal sealed class Health
{
    private const float LogSeconds = 30f;

    private long _frameTicks;
    private long _totalTicks;
    private long _worstTicks;
    private int _frames;
    private float _nextLog = LogSeconds;

    public bool Due => Time.unscaledTime >= _nextLog;
    public double AverageMs => _frames > 0 ? ToMs(_totalTicks) / _frames : 0;
    public double WorstMs => ToMs(_worstTicks);

    public long Start() => Stopwatch.GetTimestamp();

    public void Stop(long start) => _frameTicks += Stopwatch.GetTimestamp() - start;

    public void EndFrame()
    {
        _totalTicks += _frameTicks;
        if (_frameTicks > _worstTicks)
            _worstTicks = _frameTicks;
        _frameTicks = 0;
        _frames++;
    }

    public void Reset()
    {
        _nextLog = Time.unscaledTime + LogSeconds;
        _totalTicks = 0;
        _worstTicks = 0;
        _frames = 0;
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
