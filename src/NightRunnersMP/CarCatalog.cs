using BepInEx.Logging;
using PlanetJem.Core;
using UnityEngine;

namespace NightRunnersMP;

// dumps what's in the game files to the log, the unlock-everything stuff is built from this
internal static class CarCatalog
{
    private static bool _logged;

    public static void LogOnce(ManualLogSource log)
    {
        if (_logged)
            return;
        _logged = true;

        var origins = GameMaster.CarOrigins;
        if (origins == null || origins.entries_ == null)
            return;
        log.LogInfo($"Car catalog, {origins.entries_.Count} entries:");
        foreach (var entry in origins.entries_)
        {
            var data = entry.Data;
            if (data != null)
                log.LogInfo($"  {data.Model} chassis {data.Chassis} sunroof {data.HasSunroof} aiOnly {entry.AiOnly} notPlayer {data.NotAPlayerCar} beta {data.BetaCar}");
        }
    }

    public static void LogDealers(ManualLogSource log, string scene)
    {
        foreach (var spot in Object.FindObjectsOfType<homegarageSpot>(true))
            log.LogInfo($"Garage spot {spot.name} in {scene} at {spot.transform.position}, dealer {spot.carAuction != null}, entry {spot.carAuctionEntry_collider != null}");
    }
}
