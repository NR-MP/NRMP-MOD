using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// dumps what's around me to the log, for finding what closes a place or which object is a sign. walks everything so on demand only
internal static class WorldScan
{
    private const float Radius = 200f;
    private const int MaxLines = 300;
    private static readonly Regex Names = new("deal|auction|shop|store|closed|lock|barrier|gate|door|fence|sign|territory", RegexOptions.IgnoreCase);

    public static string Run(ManualLogSource log, GodConstant god, Vector3 from)
    {
        log.LogInfo($"Scan at {from}, no entry {god.noEntryToLocationsOrPause}, intro done {god.player_hasCompletedIntro}");
        var lines = 0;
        foreach (var entry in Object.FindObjectsOfType<changeScene>(true))
            log.LogInfo($"Scan entry {Describe(entry.transform, from)} scene {entry.sceneToLoad_} '{entry.changeSceneName}' canEnter {entry.canEnter} failed {entry.failedEntry} gas {entry.isGasStation}");
        foreach (var text in Object.FindObjectsOfType<TMPro.TMP_Text>(true))
        {
            if (Near(text.transform, from) && lines++ < MaxLines)
                log.LogInfo($"Scan text {Describe(text.transform, from)}: {text.text}");
        }
        foreach (var item in Object.FindObjectsOfType<Transform>(true))
        {
            if (Near(item, from) && Names.IsMatch(item.name) && lines++ < MaxLines)
                log.LogInfo($"Scan object {Describe(item, from)}");
        }
        return $"Scanned, {lines} lines in the log";
    }

    private static bool Near(Transform item, Vector3 from) => (item.position - from).sqrMagnitude < Radius * Radius;

    private static string Describe(Transform item, Vector3 from) =>
        $"{Path(item)} at {Vector3.Distance(item.position, from):F0} m, active {item.gameObject.activeInHierarchy}";

    private static string Path(Transform item)
    {
        var parent = item.parent;
        return parent != null ? $"{parent.name}/{item.name}" : item.name;
    }
}
