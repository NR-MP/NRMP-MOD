using BepInEx.Logging;
using PlanetJem.Core.FloatingOrigin;
using PlanetJem.Races;
using UnityEngine;

namespace NightRunnersMP;

// puts you back on the road if you go under the map. car hits are CarHits now, other cars can't shove you anymore
internal sealed class CrashGuard
{
    private const float GroundRay = 300f;
    private const float AboveRay = 100f;
    private const float SafeEvery = 0.5f;
    private const float UnderSeconds = 0.5f;

    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> Hits = new(64);

    private readonly ManualLogSource _log;
    private readonly System.Action<string> _notice;
    private RCC_CarControllerV3 _car;
    private Rigidbody _body;
    private float _underSince;
    private float _safeAt;
    private Vector3? _safe;
    private Vector3? _older;
    private Quaternion _safeRotation;
    private Quaternion _olderRotation;

    public CrashGuard(ManualLogSource log, System.Action<string> notice)
    {
        _log = log;
        _notice = notice;
    }

    public void Reset()
    {
        _safe = _older = null;
        _underSince = 0;
    }

    // every physics step
    public void FixedTick(RCC_CarControllerV3 car, bool free)
    {
        if (car != _car)
        {
            _car = car;
            var found = car != null ? car.GetComponent<Rigidbody>() : null;
            if (found != _body)
            {
                _body = found;
                _safe = _older = null;
                _underSince = 0;
            }
        }
        var body = _body;
        if (body == null || body.isKinematic)
            return;

        if (free)
            Rescue(car, body);
        else
            _underSince = 0;
    }

    private void Rescue(RCC_CarControllerV3 car, Rigidbody body)
    {
        var position = car.transform.position;
        if (Ground(position + Vector3.up * 2f, GroundRay, body, out _))
        {
            _underSince = 0;
            if (Time.time >= _safeAt && body.velocity.y > -5f)
                KeepSafe(car.transform);
            return;
        }
        // no road under you but sitting still is the map still streaming in, not a fall
        if (body.velocity.y > -1f)
        {
            _underSince = 0;
            return;
        }
        if (_underSince == 0)
        {
            _underSince = Time.time;
            return;
        }
        if (Time.time - _underSince < UnderSeconds)
            return;
        _underSince = 0;

        // the road you went through is right above you
        Vector3 to;
        var rotation = Quaternion.Euler(0, car.transform.eulerAngles.y, 0);
        if (Ground(position + Vector3.up * AboveRay, AboveRay, body, out var hit))
            to = hit + Vector3.up;
        else if (_older is { } older)
        {
            to = older - Offset();
            rotation = _olderRotation;
        }
        else
        {
            _log.LogWarning($"Car went under the map at {position}, nowhere to put it back");
            _notice("You fell out of the map. Teleport out with F7");
            return;
        }
        _log.LogWarning($"Car went under the map at {position}, back on the road at {to}");
        car.transform.SetPositionAndRotation(to, rotation);
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        RaceCarPhysics.SnapOntoSurface(car);
        _notice("Put you back on the road");
    }

    // two saves back, so the one we use is from before the hit
    private void KeepSafe(Transform t)
    {
        _safeAt = Time.time + SafeEvery;
        if (_safe != null)
        {
            _older = _safe;
            _olderRotation = _safeRotation;
        }
        _safe = t.position + Offset();
        _safeRotation = Quaternion.Euler(0, t.eulerAngles.y, 0);
    }

    // true world, the origin shifts while you drive
    private static Vector3 Offset() => FloatingOriginManager.Instance != null ? FloatingOriginManager.Instance.AccumulatedOriginOffset : Vector3.zero;

    // road, not a car
    private static bool Ground(Vector3 from, float length, Rigidbody own, out Vector3 point)
    {
        point = default;
        var best = float.MaxValue;
        var count = Physics.RaycastNonAlloc(from, Vector3.down, Hits, length, ~0, QueryTriggerInteraction.Ignore);
        for (var i = 0; i < count; i++)
        {
            var hit = Hits[i];
            if (hit.distance >= best)
                continue;
            // cars all carry a rigidbody, the road doesn't
            var body = hit.rigidbody;
            if (body != null && (body == own || body.GetComponent<RCC_CarControllerV3>() != null))
                continue;
            best = hit.distance;
            point = hit.point;
        }
        return best < float.MaxValue;
    }
}
