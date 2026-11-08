using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using NightRunnersMP.Shared;
using UnityEngine;

namespace NightRunnersMP;

internal sealed class Remote
{
    public byte Id;
    public string Name;
    public int Model;
    public readonly Interpolator Interp = new();
    public GameObject Placeholder;
    public GameObject Car;
    public RCC_CarControllerV3 Controller;
    public int SpawnedModel;
    public byte[] Paint;
    public byte[] PaintedFrom;
    public byte[] PaintedRead;
    public GameObject PaintedCar;
    public byte[] Build;
    public byte[] BuildOn;
    public PlayerStats Stats;
    public bool IsBot;
    public Texture2D Avatar;
    public bool HasPicture;
    public Vector3? LocalPosition;
    public Vector3 RestOffset;
    public float NextRest;
    public bool RestLogged;
    public bool Shown;
    public WalkState Walk;
    public float WalkSeen;
    public character_animController Figure;
    public Vector3 FigurePosition;
    public float FigureYaw;
    public int FigureWalkState;
    public bool FigureMoving;
    public float FigureSpeed;
    public Vector3 FigurePoolScale;
    public FigureGait Gait;
    public float NextFigure;
    public int SpawnTries;
    public int Fitted;
    public int Rebuilds;
    public float RebuiltAt;
    public byte Hinges;
    public string WheelsSeen;
    public System.Action<GameObject> Built;
    public string MapId = "";
    public string MapName = "";
    public string MapHash = "";
    public CarState Drive;
    public GameObject WheelsCar;
    public readonly List<WheelVisual> Wheels = new();
    public RCC_CarControllerV3 SoundCar;
    public float Revs;
    public byte[] LiveryFrom;
    public CarBuild.Job Livery;
    public GameObject LiveryCar;

    // from the server's crew list. a bot's comes with its stats, a player's stats can say anything
    public string ServerCrew = "";

    public string Crew => IsBot ? Stats?.Crew ?? "" : ServerCrew;

    public string Tag => Crew.Length > 0 ? $"[{Crew}] {Name}" : Name;

    public bool WantsBuild => Build != null && (IsBot || RemoteBuilds.Enabled);

    // SpawnedModel still set with no car = the game cleaned it up on us
    public bool NeedsSpawn => Model != SpawnedModel || (SpawnedModel != 0 && Car == null) || (WantsBuild && !ReferenceEquals(BuildOn, Build));

    public void ReadMap(byte[] blob)
    {
        var parts = System.Text.Encoding.UTF8.GetString(blob).Split('\n');
        if (parts.Length != 3)
            return;
        MapId = parts[0];
        MapName = SafeText.Clean(parts[1], Protocol.MaxNameLength * 2);
        MapHash = parts[2];
    }
}

// stock copies of remote cars via the game's spawner, one at a time, CarParent keeps its result in a single field
internal sealed class RemoteCars
{
    private const float SpawnTimeoutSeconds = 60f;
    private const float RetrySeconds = 3f;
    private const int MaxSpawnTries = 6;
    private const float HalfBuiltRange = 2f;
    private const string HitboxName = "NRMP hitbox";
    private const float HitboxLift = 0.25f;
    // a bit over the biggest car in the game, only there in case one odd mesh still gets through
    private static readonly Vector3 HitboxMax = new(2.3f, 2.2f, 5.6f);
    private static bool _scriptsLogged;

    private readonly ManualLogSource _log;
    private readonly Queue<Remote> _queue = new();
    private readonly HashSet<int> _failedModels = new();
    private CarParent _parent;
    private Remote _current;
    private GameObject _spawnPoint;
    private CarParent._StockCarSpawn_d__124 _spawn;
    private float _deadline;
    private float _retryAt;
    private int _wanted;
    private readonly List<(GameObject Part, int Layer)> _ownLayers = new();

    public System.Action<Remote> CarReady;

    private readonly GameSpawns _game;

    public RemoteCars(ManualLogSource log)
    {
        _log = log;
        _game = new GameSpawns(log);
    }

    public void Request(Remote remote)
    {
        if (remote.Model > 0 && !_queue.Contains(remote) && remote != _current)
            _queue.Enqueue(remote);
    }

    public void Tick(RCC_CarControllerV3 localCar, Vector3 fallbackSpawn, bool hold, meetSpot spot)
    {
        _game.Watch(localCar);
        if (_current != null)
        {
            Poll(localCar);
            if (_current == null)
                _game.OursDone();
            return;
        }
        if (hold || localCar == null || _queue.Count == 0 || Time.unscaledTime < _retryAt)
            return;
        if (_parent == null)
            _parent = Object.FindObjectOfType<CarParent>();
        if (!_game.Hold(_parent, spot))
            Begin(_queue.Dequeue(), fallbackSpawn);
    }

    public bool Busy => _current != null;

    public bool HasFailed(int model) => _failedModels.Contains(model);

    public void Forget(Remote remote)
    {
        if (remote.Car != null)
            Object.Destroy(remote.Car);
        remote.Car = null;
        remote.Controller = null;
        remote.SpawnedModel = 0;
        remote.BuildOn = null;
        remote.Fitted = 0;
        remote.NextRest = 0f;
    }

    private void Begin(Remote remote, Vector3 fallbackSpawn)
    {
        if (!remote.NeedsSpawn || _failedModels.Contains(remote.Model))
            return;
        _parent = Object.FindObjectOfType<CarParent>();
        if (_parent == null)
        {
            _log.LogWarning("No CarParent in this scene, keeping placeholder");
            return;
        }

        if (_spawnPoint == null)
            _spawnPoint = new GameObject("NRMP spawn point");
        _spawnPoint.transform.position = remote.LocalPosition ?? fallbackSpawn;

        RememberOwnLayers();
        _log.LogInfo($"Spawning model {CarKey.Model(remote.Model)} ({remote.Model}) for {remote.Name}");
        _current = remote;
        _wanted = remote.Model;
        _deadline = Time.unscaledTime + SpawnTimeoutSeconds;
        _parent.StockCarSpawn_result = null;
        // CarApplicator refuses a body that got merged into one mesh. null_part spawned most of the shell missing, this is the garage's
        // own setup, whole car with the outside left in pieces. the inside stays merged, so interior parts stay stock for now
        var setup = remote.Built != null || remote.WantsBuild ? CarParent.CarSetupType.customization_exterior : CarParent.CarSetupType.full_Combine;
        CarKey.SpawnExact(remote.Model, () =>
        {
            var routine = _parent.StockCarSpawn(CarKey.Model(remote.Model), setup, _spawnPoint.transform, true, false, true);
            _spawn = routine.TryCast<CarParent._StockCarSpawn_d__124>();
            var start = SlowStep.Start();
            _parent.StartCoroutine(routine);
            SlowStep.Stop(start, $"the game spawning {remote.Name}'s car");
        });
    }

    private void Poll(RCC_CarControllerV3 localCar)
    {
        var result = _parent != null ? _parent.StockCarSpawn_result : null;
        if (result == null)
        {
            if (_parent != null && Time.unscaledTime < _deadline && !SpawnDied)
                return;
            _log.LogError($"Spawning model {_current.Model} failed, keeping placeholder");
            _failedModels.Add(_current.Model);
            RestoreLocalPlayer(localCar);
            RemoveHalfBuilt(localCar);
            _current = null;
            return;
        }

        _parent.StockCarSpawn_result = null;
        // before the model check, a swapped spawn left my car on Traffic and there was no get in prompt in the PA
        RestoreLocalPlayer(localCar);
        if (!IsWanted(result))
            return;
        Forget(_current);
        // spawner parents it to whatever's around, that can get switched off when a race loads
        result.transform.SetParent(null);
        StartWheels(result);
        MakeGhost(result);
        if (_current.Built != null)
        {
            _current.Built(result);
            _current = null;
            return;
        }
        _current.Car = result;
        _current.Controller = result.GetComponent<RCC_CarControllerV3>();
        _current.SpawnedModel = _wanted;
        _current.SpawnTries = 0;
        _log.LogInfo($"Car ready for {_current.Name}");
        CarReady?.Invoke(_current);
        _current = null;
    }

    // state sits at the last yield while it runs, -1 once it's out. with no result that means it threw
    private bool SpawnDied => _spawn != null && _spawn.__1__state < 0;

    // a throw leaves the car where it got to, registered and live. spawned cars get switched off, so a running one on the point is it
    private void RemoveHalfBuilt(RCC_CarControllerV3 localCar)
    {
        foreach (var car in Object.FindObjectsOfType<RCC_CarControllerV3>())
        {
            if (car == localCar || !car.enabled || Vector3.Distance(car.transform.position, _spawnPoint.transform.position) > HalfBuiltRange)
                continue;
            _log.LogWarning($"Removing half built {car.name}");
            Object.Destroy(car.gameObject);
        }
    }

    // racer spawns share CarParent's origin field, so overlapping one (entering a PA) can hand back the racer's model
    private bool IsWanted(GameObject car)
    {
        var controller = car.GetComponent<RCC_CarControllerV3>();
        var origin = controller != null && controller.carLocal != null ? controller.carLocal.carOrigin : null;
        if (origin == null || origin.modelType == CarKey.Model(_wanted))
            return true;

        _log.LogWarning($"Spawn for {_current.Name} built {origin.modelType}, wanted {CarKey.Model(_wanted)}");
        Object.Destroy(car);
        _retryAt = Time.unscaledTime + RetrySeconds;
        if (++_current.SpawnTries >= MaxSpawnTries)
        {
            _log.LogError($"Giving up on model {_wanted}, keeping placeholder");
            _failedModels.Add(_wanted);
        }
        _current = null;
        return false;
    }

    // the garage setup skips the wheel start, that's what makes each wheel's corner pivot. without it the first wheel or stance fit
    // shrinks the hub down in the middle of the car. has to run before those fits, and only once, a second run stacks another pivot
    private void StartWheels(GameObject car)
    {
        var controller = car.GetComponent<RCC_CarControllerV3>();
        var front = controller != null ? controller.FrontLeftWheelCollider : null;
        if (front == null || front.newPivot != null || controller.carLocal == null)
            return;
        try
        {
            controller.carLocal.setup_brakeWC(false, true);
        }
        catch (System.Exception ex)
        {
            _log.LogWarning($"Wheel start failed on {car.name}: {ex.Message}");
        }
    }

    // network drives this, nothing of the game's should simulate it
    public static void MakeGhost(GameObject car)
    {
        var controller = car.GetComponent<RCC_CarControllerV3>();
        if (controller != null)
        {
            controller.externalController = true;
            controller.canControl = false;
            controller.enabled = false;
        }
        var body = car.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
        }
        SetCollisions(car);
        foreach (var audio in car.GetComponentsInChildren<AudioSource>(true))
            audio.enabled = false;
        LogScriptsOnce(car);
    }

    // set by NetBehaviour: does the player driving this car have collisions on too
    public static System.Func<GameObject, bool> TheyAreSolid = _ => true;

    // only when both of you have it on, otherwise one of you pushes and the other drives through. CarHits does the pushing
    public static void SetCollisions(GameObject car)
    {
        var solid = NetBehaviour.PlayerCollisions.Value && TheyAreSolid(car);
        if (solid)
            FitHitbox(car);
        foreach (var collider in car.GetComponentsInChildren<Collider>(true))
            collider.enabled = solid && !collider.isTrigger && collider.TryCast<WheelCollider>() == null;
        var hitbox = Hitbox(car);
        if (hitbox != null)
            hitbox.enabled = solid;
    }

    public static BoxCollider Hitbox(GameObject car)
    {
        var hitbox = car.transform.Find(HitboxName);
        return hitbox != null ? hitbox.GetComponent<BoxCollider>() : null;
    }

    // a trigger on a layer that can't touch yours, only there for its size. physics touching a kinematic car is the infinite weight wall
    private static void FitHitbox(GameObject car)
    {
        var root = car.transform;
        var hitbox = root.Find(HitboxName);
        if (hitbox == null)
        {
            hitbox = new GameObject(HitboxName).transform;
            hitbox.SetParent(root, false);
            hitbox.gameObject.AddComponent<BoxCollider>();
        }
        if (!TryBounds(car, out var bounds))
            return;

        // lifted so wheels don't ride up on it
        var size = Vector3.Min(bounds.size, HitboxMax);
        size.y = Mathf.Max(size.y - HitboxLift, HitboxLift);
        var collider = hitbox.GetComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = size;
        collider.center = bounds.center + Vector3.up * (HitboxLift / 2f);
        var mine = LocalLayer();
        hitbox.gameObject.layer = QuietLayer(mine, car.layer);
        NetBehaviour.Log.LogInfo($"Hitbox on {car.name}: {size} on layer {hitbox.gameObject.layer}, car on {car.layer}, mine on {mine}");
    }

    // every mesh you can see on the car, in the car's own space. hidden ones count for nothing: a fitted build leaves the
    // swapped out parts and lods switched off on the car, and those pushed the box out to the cap
    public static bool TryBounds(GameObject car, out Bounds bounds)
    {
        var root = car.transform;
        bounds = new Bounds();
        var first = true;
        foreach (var filter in car.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            var shown = filter.GetComponent<MeshRenderer>();
            if (mesh == null || shown == null || !shown.enabled || !OnUnder(filter.transform, root))
                continue;
            var box = mesh.bounds;
            for (var i = 0; i < 8; i++)
            {
                var corner = box.center + Vector3.Scale(box.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1));
                var point = root.InverseTransformPoint(filter.transform.TransformPoint(corner));
                if (first)
                    bounds = new Bounds(point, Vector3.zero);
                else
                    bounds.Encapsulate(point);
                first = false;
            }
        }
        if (!first)
            bounds.size = Vector3.Min(bounds.size, HitboxMax);
        return !first;
    }

    // switched on all the way up to the car, the car itself can be off while it waits to spawn
    private static bool OnUnder(Transform part, Transform root)
    {
        for (var at = part; at != null && at != root; at = at.parent)
            if (!at.gameObject.activeSelf)
                return false;
        return true;
    }

    private static int LocalLayer()
    {
        var local = NetBehaviour.LocalCar;
        if (local == null)
            return 0;
        foreach (var collider in local.GetComponentsInChildren<Collider>())
            if (!collider.isTrigger && collider.TryCast<WheelCollider>() == null)
                return collider.gameObject.layer;
        return local.gameObject.layer;
    }

    private static int QuietLayer(int mine, int preferred)
    {
        if (Physics.GetIgnoreLayerCollision(mine, preferred))
            return preferred;
        for (var layer = 0; layer < 32; layer++)
            if (Physics.GetIgnoreLayerCollision(mine, layer))
                return layer;
        return preferred;
    }

    // what a ghost still runs per frame, list to look through for stuff to switch off
    private static void LogScriptsOnce(GameObject car)
    {
        if (_scriptsLogged)
            return;
        _scriptsLogged = true;
        // only a log, must never block car setup
        try
        {
            var counts = new SortedDictionary<string, int>();
            foreach (var script in car.GetComponentsInChildren<MonoBehaviour>(false))
            {
                if (!script.enabled)
                    continue;
                var name = script.GetIl2CppType().Name;
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
            NetBehaviour.Log.LogInfo($"Ghost car scripts: {string.Join(", ", counts.Select(c => $"{c.Key} x{c.Value}"))}");
        }
        catch (System.Exception e)
        {
            NetBehaviour.Log.LogWarning($"Ghost script log failed: {e.Message}");
        }
    }

    // spawning might treat this as the player's car, put the real one back if so
    private void RestoreLocalPlayer(RCC_CarControllerV3 localCar)
    {
        if (localCar == null)
            return;
        var scene = RCC_SceneManager.Instance;
        if (scene != null && scene.activePlayerVehicle != localCar)
        {
            _log.LogWarning("Spawn took over the player vehicle, restoring it");
            scene.activePlayerVehicle = localCar;
        }
        var god = GodConstant.Instance;
        if (god != null && god.playerCar != localCar)
        {
            _log.LogWarning("Spawn replaced GodConstant.playerCar, restoring it");
            god.playerCar = localCar;
        }
        RestoreOwnLayers();
    }

    // a spawn can leave my own car's body collider on Traffic, the PA look ray skips that layer and there's no get in prompt
    private void RememberOwnLayers()
    {
        _ownLayers.Clear();
        var local = NetBehaviour.LocalCar;
        if (local == null)
            return;
        foreach (var part in local.GetComponentsInChildren<Transform>(true))
            _ownLayers.Add((part.gameObject, part.gameObject.layer));
    }

    private void RestoreOwnLayers()
    {
        var moved = new List<string>();
        foreach (var (part, layer) in _ownLayers)
        {
            if (part == null || part.layer == layer)
                continue;
            moved.Add($"{part.name} {LayerMask.LayerToName(part.layer)}");
            part.layer = layer;
        }
        _ownLayers.Clear();
        if (moved.Count > 0)
            _log.LogWarning($"Spawn moved {moved.Count} parts of my car to other layers, put back: {string.Join(", ", moved.Take(5))}");
    }
}
