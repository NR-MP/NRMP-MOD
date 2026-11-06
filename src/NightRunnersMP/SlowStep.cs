using System.Diagnostics;

namespace NightRunnersMP;

// health only says a frame was slow, this says which step it was
internal static class SlowStep
{
    private const double LogMs = 15;

    public static long Start() => Stopwatch.GetTimestamp();

    public static void Stop(long start, string what)
    {
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ms >= LogMs)
            NetBehaviour.Log.LogWarning($"Slow: {what} took {ms:F0} ms");
    }
}
