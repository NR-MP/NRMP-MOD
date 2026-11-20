using System;
using HarmonyLib;
using PlanetJem.Core.FloatingOrigin;
using PlanetJem.Environment;
using PlanetJem.Roads;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightRunnersMP;

internal static class OriginGuard
{
    public static bool On;

    public static Func<Scene, bool> MapScene = _ => false;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(FloatingOriginManager), "AddShiftHandler"), new HarmonyMethod(typeof(OriginGuard), nameof(AllowShiftHandler)));
        harmony.Patch(AccessTools.Method(typeof(FloatingOriginManager), nameof(FloatingOriginManager.ApplyWorldShift)), new HarmonyMethod(typeof(OriginGuard), nameof(RefreshShiftHandlers)));
    }

    private static bool AllowShiftHandler(MonoBehaviour behaviour)
    {
        if (behaviour == null)
            return false;
        if (!On)
            return true;
        var streamer = behaviour.TryCast<MapChunkStreamer>();
        if (streamer != null)
            return streamer.enabled && !MapScene(streamer.gameObject.scene);
        var roads = behaviour.TryCast<RoadNetwork>();
        return roads == null || roads.enabled;
    }

    private static void RefreshShiftHandlers(FloatingOriginManager __instance)
    {
        if (On)
            __instance.CacheShiftHandlers();
    }
}
