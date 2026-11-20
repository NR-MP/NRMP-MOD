using UnityEngine;

namespace NightRunnersMP;

// how much of a frame unity may spend unpacking assets. at High that's 50 ms, fine behind a loading screen,
// a slideshow when it's someone's car loading while you drive. back to unity's own default while one of ours loads
internal static class QuietLoad
{
    private static bool _spawning;
    private static bool _fitting;
    private static bool _logged;
    private static ThreadPriority? _before;

    public static bool Spawning
    {
        set
        {
            _spawning = value;
            Apply();
        }
    }

    public static bool Fitting
    {
        set
        {
            _fitting = value;
            Apply();
        }
    }

    private static void Apply()
    {
        var quiet = _spawning || _fitting;
        if (quiet && !_before.HasValue)
        {
            _before = Application.backgroundLoadingPriority;
            if (!_logged)
                NetBehaviour.Log.LogInfo($"The game loads at {_before.Value} priority");
            _logged = true;
            if (_before.Value > ThreadPriority.BelowNormal)
                Application.backgroundLoadingPriority = ThreadPriority.BelowNormal;
        }
        else if (!quiet && _before.HasValue)
        {
            Application.backgroundLoadingPriority = _before.Value;
            _before = null;
        }
    }
}
