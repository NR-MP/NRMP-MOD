using PlanetJem.Roads;
using PlanetJem.Roads.VehicleReset;
using UnityEngine;

namespace NightRunnersMP;

internal sealed class ResetBinding
{
    private VehicleResetController _controller;
    private RoadNetwork _network;
    private Transform _fallback;

    public bool Bind(Transform fallback)
    {
        Restore();
        var controller = VehicleResetController.instance_;
        if (controller == null)
            return false;
        _controller = controller;
        _network = controller.roadNetwork_;
        _fallback = controller.freeroamFallbackSpawn_;
        controller.freeroamTracker_?.Clear();
        controller.freeroamFallbackSpawn_ = fallback;
        return true;
    }

    public bool Restore()
    {
        if (_controller == null)
            return false;
        _controller.freeroamTracker_?.Clear();
        _controller.roadNetwork_ = _network;
        _controller.freeroamFallbackSpawn_ = _fallback;
        _controller = null;
        _network = null;
        _fallback = null;
        return true;
    }
}
