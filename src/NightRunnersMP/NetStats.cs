using LiteNetLib;
using UnityEngine;

namespace NightRunnersMP;

// what F7 shows about my own connection. the health log resets litenetlib's counters, so a count going down just starts over
internal sealed class NetStats
{
    private const float SampleSeconds = 1f;

    public int Ping = -1;
    public float DownKb;
    public float UpKb;
    public float LossPercent;

    private long _in;
    private long _out;
    private long _sent;
    private long _lost;
    private float _next;

    public void Sample(NetStatistics stats, int ping)
    {
        Ping = ping;
        if (Time.unscaledTime < _next)
            return;
        _next = Time.unscaledTime + SampleSeconds;
        DownKb = Since(ref _in, stats.BytesReceived) / 1024f / SampleSeconds;
        UpKb = Since(ref _out, stats.BytesSent) / 1024f / SampleSeconds;
        var sent = Since(ref _sent, stats.PacketsSent);
        var lost = Since(ref _lost, stats.PacketLoss);
        LossPercent = sent > 0 ? Mathf.Clamp(lost * 100f / sent, 0f, 100f) : 0f;
    }

    private static long Since(ref long last, long now)
    {
        var delta = now >= last ? now - last : now;
        last = now;
        return delta;
    }
}
