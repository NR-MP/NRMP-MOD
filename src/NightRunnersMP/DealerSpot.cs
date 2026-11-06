using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// dealer is only a map icon, nothing to enter, and the sale screen only exists in a garage. online that spot opens the cars page
internal sealed class DealerSpot
{
    private const string PointName = "CarDealer_POI";
    private const float ReachMeters = 25f;
    private const float SearchSeconds = 5f;

    private readonly ManualLogSource _log;
    private Transform _point;
    private float _nextSearch;

    public DealerSpot(ManualLogSource log)
    {
        _log = log;
    }

    public bool IsNear(Vector3 from)
    {
        if (_point == null)
        {
            if (Time.unscaledTime < _nextSearch)
                return false;
            _nextSearch = Time.unscaledTime + SearchSeconds;
            var found = GameObject.Find(PointName);
            if (found == null)
                return false;
            _point = found.transform;
            _log.LogInfo($"Car dealer spot at {_point.position}");
        }
        return (_point.position - from).sqrMagnitude < ReachMeters * ReachMeters;
    }
}
