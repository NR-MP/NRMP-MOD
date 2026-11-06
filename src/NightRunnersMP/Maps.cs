using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Logging;
using PlanetJem.Core.FloatingOrigin;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightRunnersMP;

// one folder under Maps. only map.json is required, the rest can go
internal sealed class CustomMap
{
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public string Author = "";
    public string[] Bundles;
    public string[] Scenes;
    public float[] Spawn;
    public float Yaw;
    public float[] Offset;
    public MapSpot[] Teleports;

    [JsonIgnore] public string Folder;
    [JsonIgnore] public string Hash;
    [JsonIgnore] public bool Port;

    // same map + version = same place on the wire. no rooms needed on the server
    [JsonIgnore] public string SceneName => $"map:{Id}:{Hash}";

    [JsonIgnore] public Vector3 Shift => Offset is { Length: 3 } ? new Vector3(Offset[0], Offset[1], Offset[2]) : Vector3.zero;
}

internal sealed class MapSpot
{
    public string Name = "";
    public float[] Position;
    public float Yaw;
}

// drop a folder in Maps, it shows in Teleport, same files = you meet there.
// loads next to the mountain so car, phone and save just carry on
internal sealed class Maps
{
    private const string FolderName = "Maps";
    private const string Manifest = "map.json";
    private const float UnloadSeconds = 8f;
    private const float SpawnRayHeight = 100f;
    private const float SpawnRayLength = 400f;
    private const float SpawnLift = 1f;
    private const string Readme =
        "Each map gets its own folder in here, with its map.json and bundles. It shows up in game under F7, Teleport.\n"
        + "You only see each other on a map if everyone has the same files.\n";

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    private enum Stage { Idle, Bundles, Scenes }

    private readonly ManualLogSource _log;
    private readonly Teleports _teleports;
    private readonly Action<string> _notice;
    private readonly MapPort _port;
    private readonly PortGuard _guard;
    private readonly Dictionary<string, AssetBundle> _bundles = new();
    private readonly List<string> _sceneNames = new();
    private readonly List<Scene> _scenes = new();

    private Stage _stage;
    private CustomMap _loading;
    private CustomMap _loaded;
    private int _next;
    private AssetBundleCreateRequest _opening;
    private float _openedAt;
    private AsyncOperation _load;
    private float _unloadAt;
    private Vector3? _ground;

    public List<CustomMap> Installed { get; } = new();

    public CustomMap Current { get; private set; }

    public bool Busy => _stage != Stage.Idle || _port.Moving;

    public bool OnPort => Current is { Port: true };

    public string Status { get; private set; } = "";

    // map changed
    public Action Changed;

    public static string Root => Path.Combine(Paths.GameRootPath, FolderName);

    public Maps(ManualLogSource log, Teleports teleports, Action<string> notice)
    {
        _log = log;
        _teleports = teleports;
        _notice = notice;
        _port = new MapPort(log);
        _guard = new PortGuard(log, _port, teleports, notice);
    }

    public void Scan()
    {
        Installed.Clear();
        try
        {
            Directory.CreateDirectory(Root);
            var readme = Path.Combine(Root, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, Readme);
            foreach (var folder in Directory.GetDirectories(Root))
                Read(folder);
            if (_port.Map() is { } port)
                Installed.Insert(0, port);
        }
        catch (Exception e)
        {
            _log.LogWarning($"Reading the maps folder failed: {e.Message}");
        }
    }

    private void Read(string folder)
    {
        var manifest = Path.Combine(folder, Manifest);
        if (!File.Exists(manifest))
            return;
        try
        {
            var bytes = File.ReadAllBytes(manifest);
            var map = JsonSerializer.Deserialize<CustomMap>(bytes, Json);
            map.Folder = folder;
            if (map.Id.Length == 0)
                map.Id = Path.GetFileName(folder);
            if (map.Name.Length == 0)
                map.Name = map.Id;
            map.Bundles ??= Directory.GetFiles(folder, "*.bundle").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var missing = map.Bundles.FirstOrDefault(b => !File.Exists(Path.Combine(folder, b)));
            if (missing != null)
            {
                _log.LogWarning($"Map {map.Id} is missing {missing}");
                return;
            }
            map.Hash = Hash(bytes, map);
            Installed.Add(map);
        }
        catch (Exception e)
        {
            _log.LogWarning($"Map in {folder} can't be read: {e.Message}");
        }
    }

    // file sizes instead of contents, maps can be gigabytes
    private static string Hash(byte[] manifest, CustomMap map)
    {
        using var sha = SHA256.Create();
        var text = new StringBuilder();
        foreach (var bundle in map.Bundles)
            text.Append(bundle).Append(':').Append(new FileInfo(Path.Combine(map.Folder, bundle)).Length).Append('\n');
        var all = manifest.Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray();
        return Convert.ToHexString(sha.ComputeHash(all)).Substring(0, 8).ToLowerInvariant();
    }

    public void Enter(CustomMap map)
    {
        if (Busy || _teleports.Busy || map == Current || OnPort)
            return;
        if (map.Port)
        {
            Tell(_port.Go());
            return;
        }
        _unloadAt = 0;
        if (_loaded != null && _loaded.Hash == map.Hash)
        {
            Arrive(_loaded);
            return;
        }
        Unload();
        _loading = map;
        _next = 0;
        _stage = Stage.Bundles;
        Status = $"Loading {map.Name}";
        _log.LogInfo($"Loading map {map.Id} ({map.Hash}), {map.Bundles.Length} bundles");
    }

    // scenes hang around a bit, car's still on them till the teleport lands
    public void Leave()
    {
        if (Current == null)
            return;
        if (OnPort)
        {
            Tell(_port.Back());
            return;
        }
        Current = null;
        _unloadAt = Time.unscaledTime + UnloadSeconds;
        Changed?.Invoke();
    }

    private void Tell(string reason)
    {
        if (reason != null)
            _notice(reason);
    }

    // the plugin decides when you've arrived, the Ikaho truck gets you there too
    private void FollowPort()
    {
        var on = _port.Active;
        if (on == OnPort)
            return;
        Current = on ? _port.Map() : null;
        Changed?.Invoke();
    }

    public void Tick(RCC_CarControllerV3 car)
    {
        FollowPort();
        _guard.Tick(car);
        if (_unloadAt > 0 && Time.unscaledTime >= _unloadAt)
        {
            _unloadAt = 0;
            Unload();
        }
        try
        {
            if (_stage == Stage.Bundles)
                LoadBundle();
            else if (_stage == Stage.Scenes)
                LoadScene();
        }
        catch (Exception e)
        {
            Fail($"{_loading.Name} failed to load: {e.Message}");
        }
    }

    // one at a time and async, the sync LoadFromFile is stripped from this build
    private void LoadBundle()
    {
        if (_opening != null)
        {
            if (!_opening.isDone)
                return;
            var opened = _loading.Bundles[_next - 1];
            var bundle = _opening.assetBundle;
            _opening = null;
            if (bundle == null)
            {
                Fail($"{opened} won't load in this version of the game");
                return;
            }
            _bundles[Path.Combine(_loading.Folder, opened)] = bundle;
            _log.LogInfo($"Bundle {opened} loaded in {(Time.realtimeSinceStartup - _openedAt) * 1000:F0} ms");
            return;
        }
        if (_next >= _loading.Bundles.Length)
        {
            FindScenes();
            _next = 0;
            _load = null;
            _stage = Stage.Scenes;
            return;
        }
        var name = _loading.Bundles[_next++];
        var path = Path.Combine(_loading.Folder, name);
        if (_bundles.ContainsKey(path))
            return;
        _openedAt = Time.realtimeSinceStartup;
        _opening = AssetBundle.LoadFromFileAsync(path);
        if (_opening == null)
            Fail($"{name} won't load in this version of the game");
    }

    private void FindScenes()
    {
        _sceneNames.Clear();
        if (_loading.Scenes != null)
        {
            _sceneNames.AddRange(_loading.Scenes);
            return;
        }
        throw new InvalidDataException("map.json has to list its scenes");
    }

    // newest first, a map can reuse a scene name the game already has open
    private static Scene Newest(string name)
    {
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.name == name)
                return scene;
        }
        return default;
    }

    private void LoadScene()
    {
        if (_load != null && !_load.isDone)
            return;
        if (_load != null)
        {
            var name = _sceneNames[_next - 1];
            _scenes.Add(Newest(name));
            _log.LogInfo($"Scene {name} loaded");
        }
        if (_next >= _sceneNames.Count)
        {
            Finish();
            return;
        }
        Status = $"Loading {_loading.Name}, {_next + 1} of {_sceneNames.Count}";
        _load = SceneManager.LoadSceneAsync(_sceneNames[_next++], new LoadSceneParameters(LoadSceneMode.Additive));
        if (_load == null)
            Fail($"{_sceneNames[_next - 1]} isn't in the map's bundles");
    }

    private void Finish()
    {
        var map = _loading;
        var manager = FloatingOriginManager.Instance;
        var renderers = 0;
        var pink = 0;
        _ground = null;
        foreach (var scene in _scenes)
        {
            if (!scene.IsValid())
                continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                // map coords are true world coords like a car's, and the world might be shifted right now
                var t = root.transform;
                t.position = t.position + map.Shift - manager.AccumulatedOriginOffset;
                manager.AddRoot(t);
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                    camera.enabled = false;
                foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
                    listener.enabled = false;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    renderers++;
                    var material = renderer.sharedMaterial;
                    if (material == null || material.shader == null || !material.shader.isSupported)
                        pink++;
                }
                _ground ??= FindGround(root);
            }
        }
        _log.LogInfo($"Map {map.Id} is in: {_sceneNames.Count} scenes, {renderers} renderers, {pink} with a shader this game can't draw");
        _loaded = map;
        _loading = null;
        _stage = Stage.Idle;
        Status = "";
        Arrive(map);
    }

    private static Vector3? FindGround(GameObject root)
    {
        foreach (var collider in root.GetComponentsInChildren<Collider>(false))
        {
            if (collider.isTrigger)
                continue;
            var top = collider.bounds.center + Vector3.up * SpawnRayHeight;
            if (Physics.Raycast(top, Vector3.down, out var hit, SpawnRayLength))
                return hit.point + Vector3.up * SpawnLift;
        }
        return null;
    }

    private void Arrive(CustomMap map)
    {
        Current = map;
        Changed?.Invoke();
        _teleports.Go(new Teleports.Place { Name = map.Name, Kind = "MAP", Direct = true, Pose = (out Vector3 p, out Quaternion r) => Spawn(map, out p, out r) });
    }

    private bool Spawn(CustomMap map, out Vector3 position, out Quaternion rotation)
    {
        rotation = Quaternion.Euler(0, map.Yaw, 0);
        if (map.Spawn is { Length: 3 })
        {
            position = ToScene(map, map.Spawn);
            return true;
        }
        position = _ground ?? default;
        return _ground != null;
    }

    private static Vector3 ToScene(CustomMap map, float[] at) =>
        new Vector3(at[0], at[1], at[2]) + map.Shift - FloatingOriginManager.Instance.AccumulatedOriginOffset;

    public List<Teleports.Place> Places()
    {
        var places = new List<Teleports.Place>();
        var map = Current;
        if (OnPort)
        {
            places.Add(new Teleports.Place { Name = "Back to Ikaho", Kind = "MAP", Run = Leave });
            places.AddRange(_port.Spots(Tell));
            return places;
        }
        foreach (var other in Installed.Where(m => m != map))
            places.Add(new Teleports.Place { Name = other.Name, Kind = "MAP", Run = () => Enter(other) });
        if (map == null)
            return places;
        places.Add(new Teleports.Place { Name = map.Name, Kind = "START", Direct = true, Pose = (out Vector3 p, out Quaternion r) => Spawn(map, out p, out r) });
        foreach (var spot in map.Teleports ?? Array.Empty<MapSpot>())
        {
            if (spot.Position is not { Length: 3 })
                continue;
            places.Add(new Teleports.Place
            {
                Name = spot.Name,
                Kind = "MAP",
                Direct = true,
                Pose = (out Vector3 p, out Quaternion r) =>
                {
                    p = ToScene(map, spot.Position);
                    r = Quaternion.Euler(0, spot.Yaw, 0);
                    return true;
                },
            });
        }
        return places;
    }

    private void Fail(string reason)
    {
        _log.LogError($"Map load stopped: {reason}");
        _stage = Stage.Idle;
        _loading = null;
        _loaded = null;
        _opening = null;
        DropScenes();
        Status = "";
        _notice(reason);
    }

    private void Unload()
    {
        if (_loaded == null)
            return;
        DropScenes();
        _log.LogInfo($"Map {_loaded.Id} unloaded");
        _loaded = null;
    }

    private void DropScenes()
    {
        foreach (var scene in _scenes)
            if (scene.IsValid())
                SceneManager.UnloadSceneAsync(scene);
        _scenes.Clear();
    }
}
