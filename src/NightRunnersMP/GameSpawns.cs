using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// the game's own car spawns (PA racers, traffic) share CarParent with ours. if one of ours starts while
// one of theirs is going the models get swapped and an NPC drives off in a player's car. so ours wait here
// until the game has been quiet for a bit
internal sealed class GameSpawns
{
    // after walking into a PA, the racers are queued and built one after another
    private const float EnterHoldSeconds = 8f;
    // never hold longer than this after entering, in case the racer list never empties
    private const float MaxSpotHoldSeconds = 30f;
    // no new car for this long counts as quiet
    private const float QuietSeconds = 3f;
    private const float CountEverySeconds = 1f;

    private readonly ManualLogSource _log;
    private meetSpot _spot;
    private float _spotSince;
    private int _carCount = -1;
    private float _nextCount;
    private float _quietAt;
    private bool _rebase = true;
    private string _holding;

    public GameSpawns(ManualLogSource log) => _log = log;

    // our own spawn just ended, so the next count change is ours, not the game's
    public void OursDone() => _rebase = true;

    public bool Hold(CarParent parent, meetSpot spot)
    {
        var now = Time.unscaledTime;
        if (spot != _spot)
        {
            _spot = spot;
            _spotSince = now;
        }
        Count(now);
        var reason = Reason(parent, now);
        if (reason != _holding)
        {
            if (reason != null)
                _log.LogInfo($"Holding remote car spawns, {reason}");
            else if (_holding != null)
                _log.LogInfo($"Game spawner idle, remote car spawns go ahead");
            _holding = reason;
        }
        return reason != null;
    }

    private string Reason(CarParent parent, float now)
    {
        if (parent != null && parent.loading_custom)
            return "game is loading a car customization";
        if (_spot != null)
        {
            var inside = now - _spotSince;
            if (inside < EnterHoldSeconds)
                return "just entered a PA";
            if (inside < MaxSpotHoldSeconds && RacersQueued(_spot))
                return "PA racers still building";
        }
        if (now < _quietAt)
            return "game spawned a car just now";
        return null;
    }

    private void Count(float now)
    {
        if (now < _nextCount)
            return;
        _nextCount = now + CountEverySeconds;
        var count = Object.FindObjectsOfType<RCC_CarControllerV3>().Length;
        if (!_rebase && _carCount >= 0 && count > _carCount)
            _quietAt = now + QuietSeconds;
        _carCount = count;
        _rebase = false;
    }

    // guessed from the field names, the game's racer queue. any read problem just means don't hold on it
    private static bool RacersQueued(meetSpot spot)
    {
        try
        {
            var queued = spot.queuedAIRacers_;
            return queued != null && queued.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
