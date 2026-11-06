using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using PlanetJem.Roads.FastTravel;
using PlanetJem.UI.Hints;
using UnityEngine;

namespace NightRunnersMP;

// array's Prologue plugin does its own loading, lighting, garage and races, way more than a folder map gets. it only leaves from the Ikaho truck
// though, so teleport calls that same travel straight. all looked up at runtime, the mod doesn't need the plugin to be there
internal sealed class MapPort
{
    private const string AssemblyName = "NightRunnersPrologueTravelEntry";
    private const string Namespace = "NightRunners.PrologueMapPort.";
    private const string ChassisMod = "NightRunnersAllChassis.PowerFcGui";
    private const string GoingThere = "Going to C1";
    private const string GoingBack = "Going to Ikaho";
    private const string LoadingNotice = "Map is loading, can't teleport at this moment. Wait a bit";
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly ManualLogSource _log;
    private Assembly _assembly;
    private Type _entry;
    private PropertyInfo _active;
    private PropertyInfo _transition;
    private FieldInfo _requested;
    private bool _looked;
    private CustomMap _map;

    public MapPort(ManualLogSource log) => _log = log;

    // every plugin is in by the first frame, one look is enough
    private bool Found
    {
        get
        {
            if (_looked)
                return _assembly != null;
            _looked = true;
            _assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
            if (_assembly == null)
                return false;
            _entry = _assembly.GetType(Namespace + "IkahoTravelEntry");
            var runtime = _assembly.GetType(Namespace + "MapRuntime");
            _active = runtime.GetProperty("PortSceneActive", Statics);
            _transition = runtime.GetProperty("TransitionActive", Statics);
            _requested = _entry.GetField("requestInProgress", Statics);
            _log.LogInfo("Prologue plugin found, it'll show in teleport");
            TurnTravelOn();
            StopChassisSearch();
            return true;
        }
    }

    // EnableTravel is off by default in the plugin, so a fresh install has no C1 till you edit its cfg. players get it on
    private void TurnTravelOn()
    {
        var travel = _assembly.GetType(Namespace + "Plugin")?.GetField("EnableTravel", Statics)?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
        if (travel == null || travel.Value)
            return;
        travel.Value = true;
        _log.LogInfo("Prologue: EnableTravel was off, turned it on");
    }

    // every 5 seconds the plugin scans every loaded assembly for this other mod, that's a hitch and 450 log lines a go. without that mod it can stop looking
    private void StopChassisSearch()
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(ChassisMod, false) != null))
            return;
        var next = _assembly.GetType(Namespace + "NighttuneBridge")?.GetField("nextResolve", Statics);
        next?.SetValue(null, float.MaxValue);
        _log.LogInfo($"Prologue plugin's search for {ChassisMod} {(next != null ? "stopped" : "not found")}");
    }

    public bool Active => Found && (bool)_active.GetValue(null);

    // the plugin's own number for the trip back, 9 in this build
    private const int ReturnPhase = 9;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private object Runtime => _assembly.GetType(Namespace + "Plugin")?.GetField("Runtime", Statics)?.GetValue(null);

    public bool Returning
    {
        get
        {
            var runtime = Found ? Runtime : null;
            var phase = runtime?.GetType().GetField("phase", Members);
            return phase != null && System.Convert.ToInt32(phase.GetValue(runtime)) == ReturnPhase;
        }
    }

    // gives up on the trip back the way the plugin ends one, so its overlay, pause and freeze let go
    public void Abandon()
    {
        var runtime = Runtime;
        var type = runtime?.GetType();
        var phase = type?.GetField("phase", Members);
        _log.LogWarning($"Prologue return stuck in {phase?.GetValue(runtime)}, giving up on it");
        try
        {
            phase?.SetValue(runtime, Enum.ToObject(phase.FieldType, 0));
            _active.SetValue(null, false);
            type?.GetMethod("CompleteTransition", Members)?.Invoke(runtime, null);
        }
        catch (Exception e)
        {
            _log.LogError($"Giving up on the Prologue return failed: {e}");
        }
        _requested.SetValue(null, false);
        GodConstant.Instance?.LoadingScreen_Stop(true, false);
    }

    public bool Moving => Found && ((bool)_transition.GetValue(null) || (bool)_requested.GetValue(null));

    private T Entry<T>(string field) where T : class => _entry.GetField(field, Statics).GetValue(null) as T;

    // the plugin's own files, hashed once. same plugin and same bundles = same map on the wire
    public CustomMap Map()
    {
        if (_map != null || !Found)
            return _map;
        var folder = Path.Combine(Paths.PluginPath, "PrologueMapPort", "Map");
        var layout = Path.Combine(folder, "bundle-layout.json");
        if (!File.Exists(layout))
            return null;
        using var sha = SHA256.Create();
        var all = File.ReadAllBytes(layout).Concat(File.ReadAllBytes(_assembly.Location)).ToArray();
        _map = new CustomMap
        {
            Id = "prologue",
            Name = "Prologue",
            Author = "array",
            Port = true,
            Folder = folder,
            Bundles = Array.Empty<string>(),
            Hash = Convert.ToHexString(sha.ComputeHash(all)).Substring(0, 8).ToLowerInvariant(),
        };
        return _map;
    }

    // why not, or null once it's going
    public string Go()
    {
        var scene = GodConstant.Instance?.floatingPoint_drivingScene;
        var ready = Entry<Func<bool>>("isReady");
        var travel = Entry<Action<drivingScene, FastTravelTruck>>("travel");
        if (scene == null || !scene.isActiveAndEnabled)
            return LoadingNotice;
        if (ready == null || travel == null)
            return "The C1 plugin didn't start, check the log";
        if (!ready())
            return NotReady();
        if (Moving || UIHints.IsWarnActive || FastTravelSystem.Instance == null || FastTravelSystem.Instance.IsBusy || !scene.CanNewFastTravelInteract())
            return LoadingNotice;
        return Start(GoingThere, () => travel(scene, null));
    }

    // the plugin's own ready check is three things in one, say which
    private string NotReady()
    {
        var plugin = _assembly.GetType(Namespace + "Plugin");
        var travel = plugin?.GetField("EnableTravel", Statics)?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
        if (travel != null && !travel.Value)
        {
            _log.LogWarning("Prologue: EnableTravel is off in the plugin's config");
            return "C1 travel is switched off in array's plugin config (EnableTravel)";
        }
        var runtime = plugin?.GetField("Runtime", Statics)?.GetValue(null);
        var registration = runtime?.GetType().GetField("registration", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(runtime);
        var registered = registration?.GetType().GetProperty("IsRegistered")?.GetValue(registration) as bool?;
        if (registered == false)
        {
            _log.LogWarning("Prologue: map package isn't registered, its files are missing or broken");
            return "C1 map files didn't load. Reinstall array's PrologueMapPort folder, see the C1 guide";
        }
        return LoadingNotice;
    }

    public string Back()
    {
        var scene = GodConstant.Instance?.floatingPoint_drivingScene;
        var can = Entry<Func<drivingScene, bool>>("returnAvailable");
        var back = Entry<Action<drivingScene>>("returnTravel");
        if (scene == null || can == null || back == null || Moving || !can(scene))
            return LoadingNotice;
        return Start(GoingBack, () => back(scene));
    }

    // same order the plugin's own F3 goes through
    private string Start(string hint, Action go)
    {
        _entry.GetMethod("BeginTransition", Statics).Invoke(null, new object[] { hint });
        try
        {
            go();
            return null;
        }
        catch (Exception e)
        {
            _entry.GetMethod("SetTransitionInProgress", Statics).Invoke(null, new object[] { false });
            _log.LogError($"Prologue travel failed: {e}");
            return "Prologue travel failed, check the log";
        }
    }
    // the plugin's own way in, same call its PA and garage triggers make. it walks you in with its loading, no road spot needed
    public List<Teleports.Place> Spots(Action<string> tell)
    {
        var spots = new List<Teleports.Place>();
        if (!Active)
            return spots;
        spots.Add(new Teleports.Place { Name = "Tatsumi PA", Kind = "PA", Run = () => tell(Enter("BeginMeetspotEntry")) });
        spots.Add(new Teleports.Place { Name = "South Kiba Garage", Kind = "GARAGE", Run = () => tell(Enter("BeginGarageEntry", 0f)) });
        return spots;
    }

    private string Enter(string method, params object[] args)
    {
        var runtime = _assembly.GetType(Namespace + "Plugin")?.GetField("Runtime", Statics)?.GetValue(null);
        var enter = runtime?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (enter == null)
        {
            _log.LogWarning($"Prologue plugin has no {method}");
            return "This Prologue plugin can't do that";
        }
        if (Moving)
            return LoadingNotice;
        try
        {
            enter.Invoke(runtime, args);
            _log.LogInfo($"Prologue {method}");
            return null;
        }
        catch (Exception e)
        {
            _log.LogError($"Prologue {method} failed: {e}");
            return "Prologue travel failed, check the log";
        }
    }
}
