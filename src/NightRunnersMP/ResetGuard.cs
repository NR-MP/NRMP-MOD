using System;
using BepInEx.Logging;
using HarmonyLib;
using PlanetJem.Roads.VehicleReset;
using UnityEngine;

namespace NightRunnersMP;

// The game auto-resets you while tilted, airborne or on an unknown surface, which on a folder map is the fallback spawn
// Only let the reset key through there
internal static class ResetGuard
{
    private const float LogEvery = 5f;

    public static bool OnMap;
    public static string Last = "none";
    public static float LastAt;

    private static ManualLogSource _log;
    private static float _blockedLogAt;

    public static void Install(Harmony harmony, ManualLogSource log)
    {
        _log = log;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(VehicleResetController), "RequestFreeroamResetInternal"), new HarmonyMethod(typeof(ResetGuard), nameof(Request)));
        }
        catch (Exception e)
        {
            log.LogError($"Reset guard not installed, the game's auto-reset stays on: {e.Message}");
        }
    }

    private static bool Request(VehicleResetController __instance, bool __0)
    {
        var manual = __0;
        var allow = manual || !OnMap;
        var now = Time.unscaledTime;
        if (!allow && now < _blockedLogAt)
            return false;
        Last = $"{(manual ? "manual" : "auto")} reset {(allow ? "let through" : "blocked")} ({Why(__instance)})";
        LastAt = now;
        if (!allow)
            _blockedLogAt = now + LogEvery;
        _log.LogInfo($"Game {Last}");
        return allow;
    }

    private static string Why(VehicleResetController controller)
    {
        try
        {
            var car = controller.playerCar_;
            if (car == null)
                return "no car";
            var t = car.transform;
            return $"up.z {t.up.z:F2}, grounded {(car.carData != null ? car.carData.playerIsGrounded : (bool?)null)}, surface match {(car.carLocal != null ? car.carLocal.finalSurfaceMatch : (bool?)null)}, speed {car.speed:F0}, at {t.position}";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
