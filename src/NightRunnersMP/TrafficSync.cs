using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using PlanetJem.Pooling;
using PlanetJem.Replay;
using PlanetJem.Roads.Traffic;
using UnityEngine;
using Num = System.Numerics;

namespace NightRunnersMP;

internal sealed class TrafficSync
{
    private const float WheelMetres = 150f;
    private const float GhostMetres = 700f;
    private const float StaleSeconds = 3f;
    private const int MaxGhosts = 96;
    private const float MinCapturedSpeed = 0.1f;
    private const long MaxDeriveGapMs = 250;
    private const float RespawnMetres = 50f;
    private const float StatusSeconds = 10f;
    private const float RentRetrySeconds = 1f;

    private sealed class Tracked
    {
        public ushort Id;
        public string Entry;
        public bool Seen;
        public Num.Vector3 LastWorld;
        public long LastMs;
    }

    private sealed class Ghost
    {
        public ushort Id;
        public string Entry;
        public readonly Interpolator Interp = new();
        public TrafficCar Latest;
        public float SeenAt;
        public TrafficVehicle Vehicle;
        public float RentAt;
    }

    private readonly ManualLogSource _log;
    private readonly Traffic _traffic;
    private readonly Action<NetDataWriter, DeliveryMethod> _send;
    private readonly NetDataWriter _writer = new();
    private readonly Dictionary<IntPtr, Tracked> _tracked = new();
    private readonly List<IntPtr> _goneKeys = new();
    private readonly List<KeyValuePair<ushort, string>> _spawned = new();
    private readonly List<ushort> _gone = new();
    private readonly List<TrafficCar> _cars = new();
    private readonly Dictionary<ushort, Ghost> _ghosts = new();
    private readonly Dictionary<ushort, string> _entries = new();
    private readonly List<ushort> _dropped = new();
    private readonly List<KeyValuePair<ushort, string>> _readSpawns = new();
    private readonly List<ushort> _readIds = new();
    private readonly HashSet<string> _badEntries = new();
    private readonly HashSet<string> _complaints = new();

    private byte _myId = TrafficWire.NoAuthority;
    private byte _authority = TrafficWire.NoAuthority;
    private bool _share;
    private ushort _nextId;
    private long _nextSendMs;
    private int _rented;
    private float _statusAt;
    private int _noEntry;
    private int _far;
    private int _lost;
    private int _full;
    private string _status;

    public TrafficSync(ManualLogSource log, Traffic traffic, Action<NetDataWriter, DeliveryMethod> send)
    {
        _log = log;
        _traffic = traffic;
        _send = send;
    }

    public byte Flags => (byte)(TrafficWire.FlagCapable | (Traffic.Off.Value ? 0 : TrafficWire.FlagWants));

    public bool Following => _authority != TrafficWire.NoAuthority && _authority != _myId;

    private bool Sharing => _share && _authority == _myId && _myId != TrafficWire.NoAuthority;

    public void SetMe(byte id) => _myId = id;

    public void Reset()
    {
        _authority = TrafficWire.NoAuthority;
        _share = false;
        _myId = TrafficWire.NoAuthority;
        _tracked.Clear();
        ClearGhosts();
        _traffic.Following = false;
    }

    public void OnRole(byte authority, bool share)
    {
        if (authority != _authority)
            ClearGhosts();
        if (authority != _authority || share != _share)
            _tracked.Clear();
        _authority = authority;
        _share = share;
        _traffic.Following = Following;
        _log.LogInfo(authority == TrafficWire.NoAuthority ? "Traffic: own traffic"
            : authority == _myId ? (share ? "Traffic: sharing my traffic" : "Traffic: own traffic, nobody near")
            : $"Traffic: showing traffic from player {authority}");
    }

    public void OnSpawns(NetDataReader r)
    {
        var from = r.GetByte();
        if (!TrafficTable.TryReadSpawns(r, _readSpawns) || from != _authority || !Following)
            return;
        foreach (var spawn in _readSpawns)
        {
            if (_entries.Count >= TrafficWire.MaxCars * 4 && !_entries.ContainsKey(spawn.Key))
                _entries.Clear();
            _entries[spawn.Key] = spawn.Value;
            if (_ghosts.TryGetValue(spawn.Key, out var ghost) && ghost.Entry != spawn.Value)
            {
                Release(ghost);
                ghost.Entry = spawn.Value;
            }
        }
    }

    public void OnGone(NetDataReader r)
    {
        var from = r.GetByte();
        if (!TrafficTable.TryReadIds(r, _readIds) || from != _authority || !Following)
            return;
        foreach (var id in _readIds)
        {
            _entries.Remove(id);
            if (!_ghosts.Remove(id, out var ghost))
                continue;
            Release(ghost);
        }
    }

    public void OnBatch(NetDataReader r, long nowMs)
    {
        var from = r.GetByte();
        if (from != _authority || !Following)
            return;
        var header = TrafficBatchHeader.Read(r);
        for (var i = 0; i < header.Count && r.AvailableBytes >= TrafficCar.Size; i++)
        {
            var car = TrafficCar.Read(r);
            if (!car.IsSane)
                continue;
            var ghost = GhostFor(car.Id);
            if (ghost == null)
                continue;
            ghost.Latest = car;
            ghost.SeenAt = Time.unscaledTime;
            ghost.Interp.Push(car.ToState(header.TimeMs, header.Scene), nowMs);
        }
    }

    private Ghost GhostFor(ushort id)
    {
        if (_ghosts.TryGetValue(id, out var ghost))
            return ghost;
        if (_ghosts.Count >= TrafficWire.MaxCars)
            return null;
        ghost = new Ghost { Id = id, SeenAt = Time.unscaledTime, Entry = _entries.TryGetValue(id, out var entry) ? entry : null };
        _ghosts[id] = ghost;
        return ghost;
    }

    public void TickShare(long nowMs, ushort scene, Num.Vector3 offset, List<Num.Vector3> watchers, int maxPacketBytes)
    {
        if (!Sharing || nowMs < _nextSendMs)
            return;
        _nextSendMs = Math.Max(_nextSendMs + 1000 / TrafficWire.SendRateHz, nowMs);
        var coordinator = TrafficCoordinator.Instance;
        foreach (var tracked in _tracked.Values)
            tracked.Seen = false;
        _spawned.Clear();
        _gone.Clear();
        _cars.Clear();
        if (coordinator != null && coordinator.IsTrafficEnabled)
            Gather(coordinator, nowMs, offset, watchers);
        _goneKeys.Clear();
        foreach (var pair in _tracked)
        {
            if (pair.Value.Seen)
                continue;
            _goneKeys.Add(pair.Key);
            _gone.Add(pair.Value.Id);
        }
        foreach (var key in _goneKeys)
            _tracked.Remove(key);

        if (_spawned.Count > 0)
        {
            _writer.Reset();
            _writer.Put((byte)MsgType.TrafficSpawn);
            TrafficTable.WriteSpawns(_writer, _spawned);
            _send(_writer, DeliveryMethod.ReliableOrdered);
        }
        if (_gone.Count > 0)
        {
            _writer.Reset();
            _writer.Put((byte)MsgType.TrafficGone);
            TrafficTable.WriteIds(_writer, _gone);
            _send(_writer, DeliveryMethod.ReliableOrdered);
        }
        SendCars((uint)nowMs, scene, Math.Min(maxPacketBytes, TrafficWire.MaxPacketBytes));
    }

    private void Gather(TrafficCoordinator coordinator, long nowMs, Num.Vector3 offset, List<Num.Vector3> watchers)
    {
        var active = coordinator.activeVehicles_;
        if (active == null)
            return;
        for (var i = 0; i < active.Count; i++)
        {
            var vehicle = active[i];
            if (vehicle == null || vehicle.IsReplayPresentationGhost)
                continue;
            var key = vehicle.Pointer;
            if (!_tracked.TryGetValue(key, out var tracked))
            {
                if (_tracked.Count >= TrafficWire.MaxCars)
                    continue;
                var entry = vehicle.PoolEntryId;
                if (string.IsNullOrEmpty(entry) || entry.Length > TrafficWire.MaxEntryLength)
                    continue;
                tracked = new Tracked { Id = _nextId++, Entry = entry };
                _tracked[key] = tracked;
                _spawned.Add(new KeyValuePair<ushort, string>(tracked.Id, entry));
            }
            tracked.Seen = true;
            if (TryCapture(vehicle, tracked, nowMs, offset, watchers, out var car))
                _cars.Add(car);
        }
    }

    private bool TryCapture(TrafficVehicle vehicle, Tracked tracked, long nowMs, Num.Vector3 offset, List<Num.Vector3> watchers, out TrafficCar car)
    {
        car = default;
        ReplayTrafficSnapshot snap;
        try
        {
            snap = ReplayTrafficCapture.Capture(vehicle, Time.time, false);
        }
        catch (Exception e)
        {
            Complain("capture", e);
            return false;
        }
        // capture hands back the replay's absolute position, ours adds the origin itself
        var world = ToNum(vehicle.transform.position) + offset;
        if (tracked.LastMs > 0 && Num.Vector3.DistanceSquared(world, tracked.LastWorld) > RespawnMetres * RespawnMetres)
        {
            _gone.Add(tracked.Id);
            tracked.Id = _nextId++;
            tracked.LastMs = 0;
            _spawned.Add(new KeyValuePair<ushort, string>(tracked.Id, tracked.Entry));
        }
        var velocity = ToNum(snap.Velocity);
        if (velocity.LengthSquared() < MinCapturedSpeed * MinCapturedSpeed && tracked.LastMs > 0 && nowMs - tracked.LastMs is > 0 and <= MaxDeriveGapMs)
            velocity = (world - tracked.LastWorld) / ((nowMs - tracked.LastMs) / 1000f);
        tracked.LastWorld = world;
        tracked.LastMs = nowMs;
        var wheels = false;
        foreach (var watcher in watchers)
        {
            if (Num.Vector3.DistanceSquared(watcher, world) < WheelMetres * WheelMetres)
            {
                wheels = true;
                break;
            }
        }
        car = new TrafficCar
        {
            Id = tracked.Id,
            Position = world,
            Rotation = new Num.Quaternion(snap.Rotation.x, snap.Rotation.y, snap.Rotation.z, snap.Rotation.w),
            Velocity = velocity,
            Flags = TrafficCar.PackFlags(snap.HeadlightsOn, snap.BrakeLightsOn, snap.IndicatorSide, snap.IndicatorBlinkLit, wheels),
        };
        if (!wheels)
            return car.IsSane;
        car.Wheel0 = ToWire(snap.Wheel0);
        car.Wheel1 = ToWire(snap.Wheel1);
        car.Wheel2 = ToWire(snap.Wheel2);
        car.Wheel3 = ToWire(snap.Wheel3);
        return car.IsSane;
    }

    private void SendCars(uint timeMs, ushort scene, int maxBytes)
    {
        var index = 0;
        while (index < _cars.Count)
        {
            _writer.Reset();
            _writer.Put((byte)MsgType.TrafficBatch);
            new TrafficBatchHeader { TimeMs = timeMs, Scene = scene }.Write(_writer);
            var countAt = _writer.Length - 1;
            byte count = 0;
            while (index < _cars.Count && count < byte.MaxValue && _writer.Length + _cars[index].WireSize <= maxBytes)
            {
                _cars[index].Write(_writer);
                index++;
                count++;
            }
            if (count == 0)
            {
                index++;
                continue;
            }
            _writer.Data[countAt] = count;
            _send(_writer, DeliveryMethod.Unreliable);
        }
    }

    public void Draw(double nowMs, Num.Vector3 offset, Vector3 viewer, ushort scene, bool hidden)
    {
        var coordinator = TrafficCoordinator.Instance;
        if (!Following || coordinator == null || hidden)
        {
            ReleaseAll();
            return;
        }
        // our own traffic still holds the pool until Traffic turns it off
        if (coordinator.IsTrafficEnabled)
            return;
        var pool = coordinator.TrafficPool;
        var camera = Camera.main;
        var eye = camera != null ? camera.transform.position : viewer;
        _dropped.Clear();
        _noEntry = _far = 0;
        foreach (var ghost in _ghosts.Values)
        {
            if (Time.unscaledTime - ghost.SeenAt > StaleSeconds)
            {
                _dropped.Add(ghost.Id);
                continue;
            }
            if (ghost.Entry == null)
                _noEntry++;
            if (ghost.Entry == null || _badEntries.Contains(ghost.Entry) || !ghost.Interp.HasData || ghost.Interp.Scene != scene
                || !ghost.Interp.TrySample(nowMs, out var world, out var turn))
            {
                Release(ghost);
                continue;
            }
            var position = ToUnity(world - offset);
            if ((position - viewer).sqrMagnitude > GhostMetres * GhostMetres)
            {
                _far++;
                Release(ghost);
                continue;
            }
            var rotation = new Quaternion(turn.X, turn.Y, turn.Z, turn.W);
            if (ghost.Vehicle != null && !Alive(ghost.Vehicle))
            {
                ghost.Vehicle = null;
                _rented--;
                _lost++;
            }
            if (ghost.Vehicle == null && (pool == null || _rented >= MaxGhosts || Time.unscaledTime < ghost.RentAt
                || !Rent(coordinator, pool, ghost, position, rotation)))
                continue;
            Apply(coordinator, ghost, position, rotation, eye);
        }
        foreach (var id in _dropped)
        {
            if (_ghosts.Remove(id, out var ghost))
                Release(ghost);
        }
        LogStatus();
    }

    private void LogStatus()
    {
        if (Time.unscaledTime < _statusAt)
            return;
        _statusAt = Time.unscaledTime + StatusSeconds;
        var status = $"Traffic: {_ghosts.Count} shared cars, showing {_rented}, {_entries.Count} known";
        if (_noEntry + _far + _lost + _full > 0)
            status += $", unknown {_noEntry}, too far {_far}, lost {_lost}, pool full {_full}";
        _lost = _full = 0;
        if (status == _status)
            return;
        _status = status;
        _log.LogInfo(status);
    }

    private bool Rent(TrafficCoordinator coordinator, GameObjectPool pool, Ghost ghost, Vector3 position, Quaternion rotation)
    {
        try
        {
            var context = new PoolRentContext(PoolRentMode.Traffic, position, rotation, coordinator.transform, ghost.Id, 0);
            var instance = TryRent(pool, ghost.Entry, context);
            if (instance == null)
            {
                ghost.RentAt = Time.unscaledTime + RentRetrySeconds;
                _full++;
                return false;
            }
            var vehicle = instance.GetComponent<TrafficVehicle>();
            if (vehicle == null)
            {
                pool.Return(instance);
                Bad(ghost.Entry);
                return false;
            }
            var active = coordinator.activeVehicles_;
            if (active != null && active.Contains(vehicle))
                coordinator.UnregisterVehicle(vehicle);
            vehicle.BindCoordinator(coordinator);
            vehicle.MarkReplayPresentationGhost(true);
            vehicle.SetSimulationFrozen(true);
            ghost.Vehicle = vehicle;
            _rented++;
            return true;
        }
        catch (Exception e)
        {
            Complain("rent", e);
            Bad(ghost.Entry);
            return false;
        }
    }

    private static IntPtr _tryRent;

    // PoolRentResult holds objects, and the wrapper's out copy of it overwrites managed memory, so the call is made by hand
    private static unsafe GameObject TryRent(GameObjectPool pool, string entry, PoolRentContext context)
    {
        if (_tryRent == IntPtr.Zero)
            _tryRent = IL2CPP.GetIl2CppMethod(Il2CppClassPointerStore<GameObjectPool>.NativeClassPtr, false, nameof(GameObjectPool.TryRent),
                "System.Boolean", "System.String", "PlanetJem.Pooling.PoolRentContext", "PlanetJem.Pooling.PoolRentResult&");
        const int instanceAt = 0x0, succeededAt = 0x10;
        var result = stackalloc byte[0x20];
        var args = stackalloc IntPtr[3];
        args[0] = IL2CPP.ManagedStringToIl2Cpp(entry);
        args[1] = IL2CPP.il2cpp_object_unbox(context.Pointer);
        args[2] = (IntPtr)result;
        var error = IntPtr.Zero;
        var ok = IL2CPP.il2cpp_runtime_invoke(_tryRent, pool.Pointer, (void**)args, ref error);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(error);
        if (ok == IntPtr.Zero || !*(bool*)IL2CPP.il2cpp_object_unbox(ok) || !*(bool*)(result + succeededAt))
            return null;
        var instance = *(IntPtr*)(result + instanceAt);
        return instance == IntPtr.Zero ? null : new GameObject(instance);
    }

    private void Apply(TrafficCoordinator coordinator, Ghost ghost, Vector3 position, Quaternion rotation, Vector3 eye)
    {
        var car = ghost.Latest;
        var vehicle = ghost.Vehicle;
        var snap = new ReplayTrafficSnapshot
        {
            Time = Time.time,
            LodLevel = vehicle.LodLevel,
            Position = position,
            Rotation = rotation,
            Velocity = ToUnity(car.Velocity),
            AngularVelocity = Vector3.zero,
            HeadlightsOn = car.Headlights,
            BrakeLightsOn = car.Braking,
            IndicatorSide = car.Indicator,
            IndicatorBlinkLit = car.Blink,
            HornStart = false,
        };
        if (car.HasWheels)
        {
            snap.Wheel0 = ToGame(car.Wheel0);
            snap.Wheel1 = ToGame(car.Wheel1);
            snap.Wheel2 = ToGame(car.Wheel2);
            snap.Wheel3 = ToGame(car.Wheel3);
        }
        try
        {
            ReplayPhotomodeSession.ApplyPresentationLod(vehicle, eye, ref snap, coordinator);
            var body = vehicle.Rigidbody;
            if (body != null && !body.isKinematic)
                body.isKinematic = true;
            // the physics pose zeroes velocity, which Unity warns about every frame on a kinematic body
            ReplayTrafficApplier.ApplyKinematicPose(vehicle, ref snap);
            if (vehicle.LodLevel == TrafficLodLevel.Physics && car.HasWheels)
                ReplayTrafficApplier.ApplyPhysicsWheels(vehicle, ref snap);
            ReplayTrafficApplier.ApplyPresentation(vehicle, ref snap);
        }
        catch (Exception e)
        {
            Complain("apply", e);
            Release(ghost);
            Bad(ghost.Entry);
        }
    }

    private static bool Alive(TrafficVehicle vehicle) =>
        vehicle != null && vehicle.IsRented && vehicle.IsReplayPresentationGhost && vehicle.gameObject.activeSelf;

    private void Release(Ghost ghost)
    {
        var vehicle = ghost.Vehicle;
        if (vehicle == null)
            return;
        ghost.Vehicle = null;
        _rented--;
        try
        {
            if (!vehicle.IsRented)
                return;
            vehicle.MarkReplayPresentationGhost(false);
            vehicle.ReturnToPool();
        }
        catch (Exception e)
        {
            Complain("return", e);
        }
    }

    private void ReleaseAll()
    {
        if (_rented == 0)
            return;
        foreach (var ghost in _ghosts.Values)
            Release(ghost);
        _rented = 0;
    }

    private void ClearGhosts()
    {
        ReleaseAll();
        _ghosts.Clear();
        _entries.Clear();
    }

    private void Bad(string entry)
    {
        if (entry != null && _badEntries.Add(entry))
            _log.LogWarning($"Traffic: can't show {entry}, skipping it");
    }

    private void Complain(string what, Exception e)
    {
        if (_complaints.Add(what))
            _log.LogWarning($"Traffic {what} failed: {e.GetType().Name} {e.Message}");
    }

    private static TrafficWheel ToWire(ReplayTrafficWheelSnapshot wheel) => new()
    {
        Position = ToNum(wheel.LocalPosition),
        Rotation = new Num.Quaternion(wheel.LocalRotation.x, wheel.LocalRotation.y, wheel.LocalRotation.z, wheel.LocalRotation.w),
    };

    private static ReplayTrafficWheelSnapshot ToGame(TrafficWheel wheel) => new()
    {
        LocalPosition = ToUnity(wheel.Position),
        LocalRotation = new Quaternion(wheel.Rotation.X, wheel.Rotation.Y, wheel.Rotation.Z, wheel.Rotation.W),
    };

    private static Num.Vector3 ToNum(Vector3 v) => new(v.x, v.y, v.z);

    private static Vector3 ToUnity(Num.Vector3 v) => new(v.X, v.Y, v.Z);
}
