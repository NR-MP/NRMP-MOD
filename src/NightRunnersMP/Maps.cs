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
using Cysharp.Threading.Tasks;
using PlanetJem.Core.FloatingOrigin;
using PlanetJem.Environment;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NightRunnersMP;

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

    [JsonIgnore] public string SceneName => $"map:{Id}:{Hash}";

    [JsonIgnore] public Vector3 Shift => Offset is { Length: 3 } ? new Vector3(Offset[0], Offset[1], Offset[2]) : Vector3.zero;
}

internal sealed class MapSpot
{
    public string Name = "";
    public float[] Position;
    public float Yaw;
}

internal sealed class Maps
{
    private const string FolderName = "Maps";
    private const string Manifest = "map.json";
    private const float LeaveSeconds = 1.5f;
    private const float StreamTimeout = 30f;
    private const float StreamWait = 35f;
    private const float ProbeDelay = 1f;
    private const float JumpMeters = 20f;
    private const float PortWait = 15f;
    private const float PortRetry = 0.5f;
    private const float ProbeHeight = 2f;
    private const float ProbeLength = 200f;
    private const int RoadPhysics = 17;
    private const string SpawnName = "NRMP map spawn";
    private const string GarageNotice = "Drive out of the garage first";
    private const string Readme =
        "Each map gets its own folder in here, with its map.json and bundles. It shows up in game under F7, Teleport.\n"
        + "You only see each other on a map if everyone has the same files.\n";

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    private enum Stage { Idle, Leaving, Opening, Unloading, Bundles, Scenes, Revealing, Returning, Dropping, Streaming, Closing }

    private readonly ManualLogSource _log;
    private readonly Teleports _teleports;
    private readonly Action<string> _notice;
    private readonly MapPort _port;
    private readonly PortGuard _guard;
    private readonly Haruna _haruna;
    private readonly Loading _loading;
    private readonly ResetBinding _reset = new();
    private Vector3? _lastAbsolute;
    private readonly Dictionary<string, AssetBundle> _bundles = new();
    private readonly List<string> _sceneNames = new();
    private readonly List<Scene> _scenes = new();
    private readonly List<AsyncOperation> _drops = new();
    private readonly List<AssetBundleCreateRequest> _strayBundles = new();
    private readonly List<(AsyncOperation Op, string Name)> _strayScenes = new();

    private Stage _stage;
    private CustomMap _target;
    private int _next;
    private AssetBundleCreateRequest _opening;
    private float _openedAt;
    private AsyncOperation _load;
    private bool _leftPa;
    private float _settleAt;
    private RCC_CarControllerV3 _car;
    private Vector3 _returnAbsolute;
    private Quaternion _returnRotation;
    private Teleports.Place _then;
    private UniTask _stream;
    private float _streamUntil;
    private float _probeAt;
    private Transform _ground;
    private CustomMap _portNext;
    private float _portUntil;
    private float _portTry;

    public List<CustomMap> Installed { get; } = new();

    public CustomMap Current { get; private set; }

    public bool Busy => _stage != Stage.Idle || _port.Moving;

    public bool OnPort => Current is { Port: true };

    public string Status { get; private set; } = "";

    public Action Changed;

    public static string Root => Path.Combine(Paths.GameRootPath, FolderName);

    private static Vector3 Origin => FloatingOriginManager.Instance != null ? FloatingOriginManager.Instance.AccumulatedOriginOffset : Vector3.zero;

    public Maps(ManualLogSource log, Teleports teleports, Action<string> notice)
    {
        _log = log;
        _teleports = teleports;
        _notice = notice;
        _port = new MapPort(log);
        _guard = new PortGuard(log, _port, teleports, notice);
        _haruna = new Haruna(log);
        _loading = new Loading(log);
        OriginGuard.MapScene = scene => _scenes.Any(s => s == scene);
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

    private void Step(string text) => _log.LogInfo($"Map travel: {text}");

    public void Enter(CustomMap map)
    {
        if (Busy || _teleports.Busy || map == Current || OnPort)
            return;
        if (map.Port)
        {
            if (_haruna.Captured)
            {
                _portNext = map;
                Step($"going to {map.Name}, back to Haruna first");
                Leave();
                return;
            }
            Tell(_port.Go());
            return;
        }
        if (FloatingOriginManager.Instance == null)
        {
            _notice(GarageNotice);
            return;
        }
        if (map.Spawn is not { Length: 3 })
        {
            _notice($"{map.Name} has no spawn in its map.json");
            return;
        }
        var god = GodConstant.Instance;
        if (god == null || _car == null)
            return;
        _target = map;
        if (god.scene_currentType == GodConstant.Scene_currentType.MEETSPOT)
        {
            _leftPa = false;
            _settleAt = 0;
            _stage = Stage.Leaving;
            Status = $"Going to {map.Name}";
            return;
        }
        if (god.scene_currentType != GodConstant.Scene_currentType.FREEROAM)
        {
            _notice("Drive out first, then teleport");
            _target = null;
            return;
        }
        Open();
    }

    public void Leave(Teleports.Place then = null)
    {
        if (OnPort)
        {
            Tell(_port.Back());
            return;
        }
        if (Busy || !_haruna.Captured)
            return;
        _then = then;
        StartReturn();
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
        _car = car;
        WatchExit();
        ResetGuard.OnMap = Current is { Port: false };
        WatchJump(car);
        FollowPort();
        GoPortLater();
        _guard.Tick(car);
        DropBundles();
        if (_probeAt > 0 && Time.unscaledTime >= _probeAt)
        {
            _probeAt = 0;
            Probe();
        }
        if (_stage == Stage.Idle)
            return;
        var returning = _stage >= Stage.Returning;
        try
        {
            switch (_stage)
            {
                case Stage.Leaving: Leaving(); break;
                case Stage.Opening: Opening(); break;
                case Stage.Unloading: Unloading(); break;
                case Stage.Bundles: LoadBundle(); break;
                case Stage.Scenes: LoadScene(); break;
                case Stage.Revealing: Revealing(); break;
                case Stage.Returning: Returning(); break;
                case Stage.Dropping: Dropping(); break;
                case Stage.Streaming: Streaming(); break;
                case Stage.Closing: Closing(); break;
            }
        }
        catch (Exception e)
        {
            _log.LogError(e.ToString());
            if (returning)
                Abort();
            else
                Fail($"{_target?.Name} failed to load: {e.Message}");
        }
    }

    private void Leaving()
    {
        var god = GodConstant.Instance;
        if (god == null)
            return;
        var type = god.scene_currentType;
        if (type == GodConstant.Scene_currentType.MEETSPOT)
        {
            if (!_leftPa)
            {
                _leftPa = true;
                _teleports.LeavePa();
                Step("leaving the PA first");
            }
            return;
        }
        if (type != GodConstant.Scene_currentType.FREEROAM)
        {
            Fail("Drive out first, then teleport");
            return;
        }
        if (_settleAt == 0)
            _settleAt = Time.unscaledTime + LeaveSeconds;
        if (Time.unscaledTime < _settleAt || _car == null || _teleports.Busy)
            return;
        Open();
    }

    private void Open()
    {
        if (!_haruna.Captured)
        {
            _returnAbsolute = _car.transform.position + Origin;
            _returnRotation = _car.transform.rotation;
        }
        if (!_loading.Begin(_car.gameObject))
        {
            Fail("The game's loading screen isn't there");
            return;
        }
        OriginGuard.On = true;
        _stage = Stage.Opening;
        Status = $"Going to {_target.Name}";
        Step($"1 loading screen up for {_target.Id} ({_target.Hash}), vehicles frozen with freezeAllVehicles, return pose {_returnAbsolute}");
    }

    private void Opening()
    {
        if (!_loading.Ready)
            return;
        Step("2 screen is black");
        if (Current != null)
        {
            Step($"2 leaving {Current.Name} first");
            _reset.Restore();
            Current = null;
            Changed?.Invoke();
            DropScenes();
        }
        if (!_haruna.Captured)
        {
            _haruna.Capture();
            _haruna.Suspend();
        }
        _stage = Stage.Unloading;
    }

    private void Unloading()
    {
        _haruna.Tick();
        if (_haruna.Unloading || Pending())
            return;
        _next = 0;
        _opening = null;
        _stage = Stage.Bundles;
        Step($"3 loading {_target.Bundles.Length} bundles");
    }

    // one at a time and async, the sync LoadFromFile is stripped from this build
    private void LoadBundle()
    {
        if (_opening != null)
        {
            if (!_opening.isDone)
                return;
            var opened = _target.Bundles[_next - 1];
            var bundle = _opening.assetBundle;
            _opening = null;
            if (bundle == null)
            {
                Fail($"{opened} won't load in this version of the game");
                return;
            }
            _bundles[Path.Combine(_target.Folder, opened)] = bundle;
            Step($"3 bundle {opened} loaded in {(Time.realtimeSinceStartup - _openedAt) * 1000:F0} ms");
            return;
        }
        if (_next >= _target.Bundles.Length)
        {
            if (_target.Scenes is not { Length: > 0 })
                throw new InvalidDataException("map.json has to list its scenes");
            _sceneNames.Clear();
            _sceneNames.AddRange(_target.Scenes);
            _next = 0;
            _load = null;
            _stage = Stage.Scenes;
            return;
        }
        var name = _target.Bundles[_next++];
        var path = Path.Combine(_target.Folder, name);
        if (_bundles.ContainsKey(path))
            return;
        _openedAt = Time.realtimeSinceStartup;
        _opening = AssetBundle.LoadFromFileAsync(path);
        if (_opening == null)
            Fail($"{name} won't load in this version of the game");
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
            Step($"3 scene {name} loaded");
        }
        if (_next >= _sceneNames.Count)
        {
            Arrive();
            return;
        }
        Status = $"Loading {_target.Name}, {_next + 1} of {_sceneNames.Count}";
        _load = SceneManager.LoadSceneAsync(_sceneNames[_next++], new LoadSceneParameters(LoadSceneMode.Additive));
        if (_load == null)
            Fail($"{_sceneNames[_next - 1]} isn't in the map's bundles");
    }

    private void Arrive()
    {
        var map = _target;
        if (FloatingOriginManager.Instance == null)
        {
            Fail(GarageNotice);
            return;
        }
        var origin = Origin;
        int added = 0, carried = 0, renderers = 0, pink = 0;
        Transform first = null;
        foreach (var scene in _scenes)
        {
            if (!scene.IsValid())
                continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                var binding = root.GetComponent<FloatingOriginRoot>();
                if (binding == null)
                {
                    // Our bundles can't carry the game's binding, so bake the map.json offset in like C1's heightOffset
                    root.transform.position += map.Shift;
                    binding = root.AddComponent<FloatingOriginRoot>();
                    added++;
                }
                else
                {
                    carried++;
                }
                binding.AlignWithOriginOffset(origin);
                first ??= root.transform;
                foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                    if (!collider.isTrigger)
                        collider.gameObject.layer = Layer;
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
            }
        }
        _ground = first;
        Physics.SyncTransforms();
        Step($"3 {_scenes.Count} scenes in, {added} roots given a FloatingOriginRoot, {carried} carried one, aligned to origin offset {origin}, {renderers} renderers, {pink} with a shader this game can't draw");

        _haruna.Activate(_scenes.FirstOrDefault());

        Spawn(map, out var position, out var rotation);
        var spawn = new GameObject(SpawnName);
        if (_scenes.Count > 0 && _scenes[0].IsValid())
            SceneManager.MoveGameObjectToScene(spawn, _scenes[0]);
        spawn.transform.SetPositionAndRotation(position, rotation);
        if (first != null)
            spawn.transform.SetParent(first, true);
        Step(_reset.Bind(spawn.transform)
            ? $"5 reset controller tracker cleared, fallback spawn at {position}"
            : "5 no reset controller to bind");

        MoveCar(position, rotation);
        Step($"6 car at spawn {position} yaw {map.Yaw}, velocities zeroed, transforms synced");
        RestoreDriving();

        _target = null;
        Current = map;
        Changed?.Invoke();
        _loading.Release();
        _probeAt = Time.unscaledTime + ProbeDelay;
        _stage = Stage.Revealing;
        Step("7 vehicles unfrozen, loading screen let go");
    }

    private void Revealing()
    {
        if (!_loading.Closed())
            return;
        _stage = Stage.Idle;
        Status = "";
        Step($"7 loading screen gone, on {Current?.Name}");
    }

    private void StartReturn()
    {
        _target = null;
        if (_car != null && _loading.Begin(_car.gameObject))
            Step("R1 loading screen up, vehicles frozen");
        else
            Step("R1 no loading screen, returning anyway");
        _stage = Stage.Returning;
        Status = "Back to Haruna";
    }

    private void Returning()
    {
        if (!_loading.Ready)
            return;
        _haruna.Restore();
        Step(_reset.Restore() ? "R2 reset controller back on Haruna" : "R2 reset controller wasn't bound");
        var was = Current;
        Current = null;
        if (was != null)
            Changed?.Invoke();
        DropScenes();
        _stage = Stage.Dropping;
    }

    private void Dropping()
    {
        if (Pending())
            return;
        FloatingOriginManager.Instance?.CacheShiftHandlers();
        Step("R3 map scenes unloaded");
        var position = _returnAbsolute - Origin;
        MoveCar(position, _returnRotation);
        Step($"R4 car back at {position}");
        if (_haruna.Streamed)
        {
            MapChunkStreamer.BeginStreaming();
            _stream = MapChunkStreamer.ResyncProbeZonesAndWaitAsync(new Il2CppSystem.Nullable<Vector3>(position), StreamTimeout, true, Il2CppSystem.Threading.CancellationToken.None);
            _streamUntil = Time.unscaledTime + StreamWait;
            Step("R5 streaming back on, waiting for the chunks around the car");
        }
        _stage = Stage.Streaming;
    }

    private void Streaming()
    {
        if (_haruna.Streamed && _stream.Status == UniTaskStatus.Pending && Time.unscaledTime < _streamUntil)
            return;
        if (_haruna.Streamed)
            Step(_stream.Status == UniTaskStatus.Pending ? "R5 chunks took too long, going anyway" : $"R5 chunks around the car are in ({_stream.Status})");
        _haruna.Clear();
        RestoreDriving();
        _loading.Release();
        _stage = Stage.Closing;
        Step("R6 vehicles unfrozen, loading screen let go");
    }

    private void Closing()
    {
        if (!_loading.Closed())
            return;
        OriginGuard.On = false;
        _stage = Stage.Idle;
        Status = "";
        Step("R6 loading screen gone, back on Haruna");
        if (_portNext != null)
        {
            _portUntil = Time.unscaledTime + PortWait;
            _portTry = 0;
        }
        var then = _then;
        _then = null;
        if (then != null)
            _teleports.Go(then);
    }

    private void Abort()
    {
        _log.LogError("Map travel: return failed, putting Haruna back as far as possible");
        try
        {
            _haruna.Restore();
            _reset.Restore();
            DropScenes();
            if (_haruna.Streamed)
                MapChunkStreamer.BeginStreaming();
            MoveCar(_returnAbsolute - Origin, _returnRotation);
            RestoreDriving();
        }
        catch (Exception e)
        {
            _log.LogError(e.ToString());
        }
        _haruna.Clear();
        _loading.End();
        OriginGuard.On = false;
        Current = null;
        Changed?.Invoke();
        _then = null;
        _portNext = null;
        _stage = Stage.Idle;
        Status = "";
    }

    // Wait for the return, the game stays busy for a moment after its loading screen
    private void GoPortLater()
    {
        if (_portNext == null || _stage != Stage.Idle || _teleports.Busy || Time.unscaledTime < _portTry)
            return;
        var reason = _port.Go();
        if (reason == MapPort.LoadingNotice && Time.unscaledTime < _portUntil)
        {
            _portTry = Time.unscaledTime + PortRetry;
            return;
        }
        Step(reason == null ? $"{_portNext.Name} travel started" : $"{_portNext.Name} travel didn't start: {reason}");
        _portNext = null;
        Tell(reason);
    }

    private void WatchJump(RCC_CarControllerV3 car)
    {
        if (car == null)
        {
            _lastAbsolute = null;
            return;
        }
        var at = car.transform.position + Origin;
        if (_lastAbsolute is { } was && (at - was).sqrMagnitude > JumpMeters * JumpMeters)
        {
            var reset = ResetGuard.LastAt > 0 ? $"{ResetGuard.Last} {Time.unscaledTime - ResetGuard.LastAt:F1} s ago" : "none";
            _log.LogInfo($"Car moved {Vector3.Distance(was, at):F0} m in one frame, {was} to {at} (absolute), on {Current?.Name ?? "Haruna"}, travel {_stage}, last game reset: {reset}\n{Environment.StackTrace}");
        }
        _lastAbsolute = at;
    }

    // C1's hotkey or a garage can pull the car off a folder map without us, so Haruna has to be restored first
    private void WatchExit()
    {
        if (_stage != Stage.Idle || Current is not { Port: false })
            return;
        var type = GodConstant.Instance?.scene_currentType;
        var why = _port.Moving || _port.Active ? "C1 travel started"
            : type is GodConstant.Scene_currentType.GARAGE or GodConstant.Scene_currentType.STORAGE ? $"{type} entered"
            : FloatingOriginManager.Instance == null ? "floating origin gone"
            : null;
        if (why == null)
            return;
        _log.LogWarning($"Map travel: {why} while on {Current.Name}, dropping the map");
        _haruna.Restore();
        _reset.Restore();
        DropScenes();
        if (_haruna.Streamed)
            MapChunkStreamer.BeginStreaming();
        _haruna.Clear();
        OriginGuard.On = false;
        _probeAt = 0;
        _portNext = null;
        Current = null;
        Changed?.Invoke();
    }

    private void Fail(string reason)
    {
        _log.LogError($"Map travel stopped: {reason}");
        _portNext = null;
        _notice(reason);
        if (_opening != null)
            _strayBundles.Add(_opening);
        if (_load != null && !_load.isDone && _next > 0)
            _strayScenes.Add((_load, _sceneNames[_next - 1]));
        _opening = null;
        _load = null;
        _probeAt = 0;
        if (_haruna.Captured)
        {
            _then = null;
            if (!_loading.Active)
                StartReturn();
            else
            {
                _target = null;
                _stage = Stage.Returning;
                Status = "Back to Haruna";
            }
            return;
        }
        _loading.End();
        OriginGuard.On = false;
        _target = null;
        _stage = Stage.Idle;
        Status = "";
    }

    private void MoveCar(Vector3 position, Quaternion rotation)
    {
        if (_car == null)
            return;
        _car.transform.SetPositionAndRotation(position, rotation);
        var body = _car.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.position = position;
            body.rotation = rotation;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
    }

    private void RestoreDriving()
    {
        var god = GodConstant.Instance;
        if (god == null || _car == null)
            return;
        var drive = god.floatingPoint_drivingScene;
        if (drive != null)
        {
            drive.playerCar = _car;
            drive.player_changeScene = false;
            drive.changeScene_current = null;
            if (drive.player_mainCam != null)
                drive.player_mainCam.SetTarget(_car.gameObject);
        }
        god.playerCar = _car;
        god.currenthomegarage = null;
        god.currentmeetspot = null;
        god.scene_currentType = GodConstant.Scene_currentType.FREEROAM;
        god.cinematic_playing = false;
        var rcc = RCC_SceneManager.Instance;
        if (rcc != null)
            rcc.RegisterPlayer(_car);
        Step("driving restored, FREEROAM, car registered with RCC");
    }

    private void Probe()
    {
        if (_car == null)
            return;
        var position = _car.transform.position;
        Surroundings(position);
        var ground = _ground != null ? $", map ground root {_ground.name} at {_ground.position}, car Y minus ground root Y {position.y - _ground.position.y:F2}" : ", no map ground root";
        var hits = Physics.RaycastAll(position + Vector3.up * ProbeHeight, Vector3.down, ProbeLength, ~0, QueryTriggerInteraction.Ignore);
        RaycastHit? nearest = null;
        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(_car.transform))
                continue;
            if (nearest == null || hit.distance < nearest.Value.distance)
                nearest = hit;
        }
        if (nearest is not { } under)
        {
            Step($"1 s after arrival car at {position}, nothing under it within {ProbeLength} m, origin offset {Origin}{ground}");
            return;
        }
        var go = under.collider.gameObject;
        Step($"1 s after arrival car at {position}, under it {go.name} in scene {go.scene.name} on layer {LayerMask.LayerToName(go.layer)} ({go.layer}), {under.distance - ProbeHeight:F2} m down, origin offset {Origin}{ground}");
    }

    private void Surroundings(Vector3 position)
    {
        var body = _car.GetComponent<Rigidbody>();
        var state = body != null ? $"kinematic {body.isKinematic}, sleeping {body.IsSleeping()}, velocity {body.velocity}" : "no rigidbody";
        var colliders = Physics.OverlapSphere(position, 3f, ~0, QueryTriggerInteraction.Ignore)
            .Where(c => c != null && !c.transform.IsChildOf(_car.transform)).Take(5).ToList();
        var ray = new Ray(position + Vector3.up * ProbeHeight, Vector3.down);
        // Collider.Raycast skips the scene query tree, so a hit here and a RaycastAll miss means the tree is broken, not the mesh
        var touching = colliders.Select(c =>
        {
            var mesh = c.TryCast<MeshCollider>();
            var own = c.Raycast(ray, out var h, ProbeLength) ? $"own ray {h.distance - ProbeHeight:F2} m down" : "own ray misses";
            var info = mesh != null ? $", convex {mesh.convex}, cooking {mesh.cookingOptions}, mesh {(mesh.sharedMesh != null ? $"{mesh.sharedMesh.name} readable {mesh.sharedMesh.isReadable}" : "none")}" : "";
            return $"{c.name} ({c.gameObject.scene.name}, layer {c.gameObject.layer}, {c.GetIl2CppType().Name}, enabled {c.enabled}, bounds {c.bounds.min.y:F2}..{c.bounds.max.y:F2}, {own}{info})";
        }).ToList();
        var backfaces = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        var all = Physics.RaycastAll(ray, ProbeLength, ~0, QueryTriggerInteraction.Ignore);
        Physics.queriesHitBackfaces = backfaces;
        var back = string.Join(", ", all.Where(x => x.collider != null).OrderBy(x => x.distance).Take(4).Select(x => $"{x.collider.name}{(x.collider.transform.IsChildOf(_car.transform) ? " (car)" : "")} {x.distance - ProbeHeight:F2}"));
        back = back.Length > 0 ? back : "nothing";
        var near = UnityEngine.Object.FindObjectsOfType<Renderer>()
            .Where(r => r != null && r.enabled && !r.transform.IsChildOf(_car.transform))
            .Where(r => { var b = r.bounds; return Mathf.Abs(b.center.x - position.x) <= b.extents.x + 30f && Mathf.Abs(b.center.z - position.z) <= b.extents.z + 30f && Mathf.Abs(b.center.y - position.y) <= b.extents.y + 30f; })
            .GroupBy(r => r.gameObject.scene.name)
            .Select(g => $"{g.Key} {g.Count()} ({string.Join(", ", g.Take(3).Select(r => $"{r.name} at {r.bounds.center}"))})").ToList();
        Step($"around the car: body {state}; colliders within 3 m: {(touching.Count > 0 ? string.Join("; ", touching) : "none")}; RaycastAll with backfaces: {back}; renderers within 30 m by scene: {(near.Count > 0 ? string.Join("; ", near) : "none")}");
    }

    private bool Pending()
    {
        _drops.RemoveAll(op => op == null || op.isDone);
        return _drops.Count > 0;
    }

    private static int Layer
    {
        get
        {
            var layer = LayerMask.NameToLayer("Road Physics");
            return layer >= 0 ? layer : RoadPhysics;
        }
    }

    private static void Spawn(CustomMap map, out Vector3 position, out Quaternion rotation)
    {
        rotation = Quaternion.Euler(0, map.Yaw, 0);
        position = ToScene(map, map.Spawn);
    }

    private static Vector3 ToScene(CustomMap map, float[] at) => new Vector3(at[0], at[1], at[2]) + map.Shift - Origin;

    public List<Teleports.Place> Places()
    {
        var places = new List<Teleports.Place>();
        var map = Current;
        if (OnPort)
        {
            places.Add(new Teleports.Place { Name = "Back to Ikaho", Kind = "MAP", Run = () => Leave() });
            places.AddRange(_port.Spots(Tell));
            return places;
        }
        foreach (var other in Installed.Where(m => m != map))
            places.Add(new Teleports.Place { Name = other.Name, Kind = "MAP", Run = () => Enter(other) });
        if (map == null)
            return places;
        places.Add(new Teleports.Place
        {
            Name = map.Name,
            Kind = "START",
            Direct = true,
            Pose = (out Vector3 p, out Quaternion r) =>
            {
                Spawn(map, out p, out r);
                return true;
            },
        });
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
                    r = Quaternion.Euler(0, spot.Yaw, 0);
                    p = ToScene(map, spot.Position);
                    return true;
                },
            });
        }
        return places;
    }

    // unity won't open a second bundle with the same name, and a map update needs its new bundle read, so they go with the scenes
    private void DropBundles()
    {
        for (var i = _strayScenes.Count - 1; i >= 0; i--)
        {
            var (op, name) = _strayScenes[i];
            if (op != null && !op.isDone)
                continue;
            _strayScenes.RemoveAt(i);
            var scene = Newest(name);
            if (scene.IsValid() && scene.isLoaded && !_scenes.Any(s => s == scene))
                _drops.Add(SceneManager.UnloadSceneAsync(scene));
        }
        if (_stage is Stage.Bundles or Stage.Scenes || _scenes.Count > 0 || _strayScenes.Count > 0 || Pending())
            return;
        for (var i = _strayBundles.Count - 1; i >= 0; i--)
        {
            var op = _strayBundles[i];
            if (op != null && !op.isDone)
                continue;
            _strayBundles.RemoveAt(i);
            if (op?.assetBundle != null)
                op.assetBundle.Unload(true);
        }
        if (_bundles.Count == 0)
            return;
        foreach (var bundle in _bundles.Values)
            if (bundle != null)
                bundle.Unload(true);
        _log.LogInfo($"Map travel: {_bundles.Count} bundles unloaded");
        _bundles.Clear();
    }

    private void DropScenes()
    {
        foreach (var scene in _scenes)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                continue;
            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null)
                _drops.Add(op);
        }
        _scenes.Clear();
    }
}
