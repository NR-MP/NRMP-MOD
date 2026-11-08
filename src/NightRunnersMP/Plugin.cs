using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using NightRunnersMP.Shared;

namespace NightRunnersMP;

[BepInPlugin(Guid, Name, SemVer)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.nightrunners.multiplayer";
    public const string Name = "Night Runners Multiplayer";
    public const string Version = "0.1.4b2";
    // bepinex wants plain semver, the b doesn't parse
    public const string SemVer = "0.1.4";

    public override void Load()
    {
        NetBehaviour.Log = Log;
        NetBehaviour.Host = Config.Bind("Server", "Host", "127.0.0.1", "Server address.").Value;
        NetBehaviour.Port = Config.Bind("Server", "Port", Protocol.DefaultPort, "Server UDP port.").Value;
        NetBehaviour.JoinKey = Config.Bind("Server", "JoinKey", "", "Key shared with your friends.").Value;

        NetBehaviour.SpawnRealCars = Config.Bind("Player", "SpawnRealCars", true, "Show other players as real cars instead of boxes.").Value;

        NetBehaviour.SkipIntro = Config.Bind("Player", "SkipIntro", true, "Skip the intro video at startup.").Value;

        NetBehaviour.PlayerCollisions = Config.Bind("Player", "PlayerCollisions", false, "Other players' cars are solid. Set in the game under F7, Settings.");

        Traffic.Off = Config.Bind("Player", "TrafficOff", false, "No traffic on your screen, others still have theirs. Set in the game under F7, Settings.");

        RemoteBuilds.Enabled = Config.Bind("Player", "FitPlayersParts", true, "Fit other players' parts to their cars. Not yet tried in the game.").Value;

        NetBehaviour.SpawnStop = Config.Bind("Player", "SpawnStop", GodConstant.MeetSpots_All.MH_IKAHO_PA,
            "Parking stop to start at when choosing Multiplayer.").Value;

        var harmony = new Harmony(Guid);
        Loading.Install(harmony);
        OriginGuard.Install(harmony);
        ResetGuard.Install(harmony, Log);

        AddComponent<NetBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded, server {NetBehaviour.Host}:{NetBehaviour.Port}");
    }
}
