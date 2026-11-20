using Rewired;
using UnityEngine;

namespace NightRunnersMP;

internal static class EscGuard
{
    private const float MaxHoldSeconds = 2f;
    private const int FramesAfterRelease = 3;

    private static bool _holding;
    private static bool _released;
    private static int _releasedFrame;
    private static float _since;
    private static bool _setPause;

    public static bool Holding => _holding;

    public static void Hold()
    {
        _since = Time.unscaledTime;
        _released = false;
        _holding = true;
        KeepPauseOff();
    }

    public static void OnEvent(Event e)
    {
        if (_holding && !_released && e.type == EventType.KeyUp && e.keyCode == KeyCode.Escape)
        {
            _released = true;
            _releasedFrame = Time.frameCount;
        }
    }

    public static void Tick(bool keyboardFree)
    {
        if (!_holding)
            return;
        var waiting = _released
            ? Time.frameCount - _releasedFrame < FramesAfterRelease
            : Time.unscaledTime - _since < MaxHoldSeconds;
        if (waiting)
        {
            KeepPauseOff();
            return;
        }
        _holding = false;
        var god = GodConstant.Instance;
        if (_setPause && god != null)
            god.no_pauseGame = false;
        _setPause = false;
        if (keyboardFree && ReInput.isReady)
            ReInput.controllers.Keyboard.enabled = true;
    }

    private static void KeepPauseOff()
    {
        var god = GodConstant.Instance;
        if (god == null || god.no_pauseGame)
            return;
        god.no_pauseGame = true;
        _setPause = true;
    }
}
