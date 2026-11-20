using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

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
    private int _netGcs;
    private int _gameGcs;
    private int _frameNetGcs;
    private int _frameGameGcs;
    private bool _noGameGcs;

    public bool Due => Time.unscaledTime >= _nextLog;
    public double AverageMs => _frames > 0 ? ToMs(_totalTicks) / _frames : 0;
    public double WorstMs => ToMs(_worstTicks);
    public int WorstNetGcs { get; private set; }
    public int WorstGameGcs { get; private set; }
    public long NetHeapMb => GC.GetTotalMemory(false) >> 20;

    public long GameHeapMb
    {
        get
        {
            try
            {
                return Profiler.GetMonoUsedSizeLong() >> 20;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    public string GameCollector
    {
        get
        {
            try
            {
                return GarbageCollector.isIncremental ? "incremental" : "not incremental";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    public long Start()
    {
        _netGcs = GC.CollectionCount(0);
        _gameGcs = GameGcs();
        return Stopwatch.GetTimestamp();
    }

    public void Stop(long start)
    {
        _frameTicks += Stopwatch.GetTimestamp() - start;
        _frameNetGcs += GC.CollectionCount(0) - _netGcs;
        _frameGameGcs += GameGcs() - _gameGcs;
    }

    public void EndFrame()
    {
        _totalTicks += _frameTicks;
        if (_frameTicks > _worstTicks)
        {
            _worstTicks = _frameTicks;
            WorstNetGcs = _frameNetGcs;
            WorstGameGcs = _frameGameGcs;
        }
        _frameTicks = 0;
        _frameNetGcs = 0;
        _frameGameGcs = 0;
        _frames++;
    }

    public void Reset()
    {
        _nextLog = Time.unscaledTime + LogSeconds;
        _totalTicks = 0;
        _worstTicks = 0;
        WorstNetGcs = 0;
        WorstGameGcs = 0;
        _frames = 0;
    }

    // a collection that starts inside mod code stops the whole frame and gets billed to the mod
    private int GameGcs()
    {
        if (_noGameGcs)
            return 0;
        try
        {
            return Il2CppSystem.GC.CollectionCount(0);
        }
        catch (Exception)
        {
            _noGameGcs = true;
            return 0;
        }
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
