using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// @nonrog his fix. a kinematic car is a wall of infinite weight, so other cars don't touch yours anymore and each game only pushes its own car.
// on a touch it works the hit out from how fast you close and both weights, pushes you and tells the other game,
// which pushes theirs once the cars touch on their screen too. the same bump seen from both sides only counts once
internal sealed class CarHits
{
    private const float Bounce = 0.3f;
    private const float MinClosing = 0.5f;
    private const float MaxKick = 20f;
    // their screen shows me a bit behind, so a touch there can sit a little apart from mine
    private const float Reach = 0.4f;
    private const float HoldSeconds = 0.5f;
    private const float BumpSeconds = 0.5f;
    private const float SendGap = 0.1f;
    private const float SeparateSpeed = 2f;
    private const float SeparatePerMetre = 4f;
    private const float FallbackMass = 1300f;
    private const int HitBytes = 1 + 4 * 4;
    // the other game's word on speed and weight, held to what its car is seen doing on my screen. a modded game can't launch me
    private const float MinMass = 400f;
    private const float MaxMass = 4000f;
    private const float SpeedWindow = 0.1f;
    private const float SpeedSlack = 5f;
    private const float UnknownSpeed = 120f;
    private const float ClosingRatio = 1.3f;
    private const float ClosingSlack = 3f;
    private const float MinNormalDot = 0.5f;

    private sealed class Bump
    {
        public float Until;
        public float Pushed;
        public float SentAt = -1f;
    }

    private sealed class Hit
    {
        public Vector3 Normal;
        public float Closing;
        public float Mass;
        public float At;
    }

    private sealed class Seen
    {
        public Vector3 Position;
        public float At;
        public float Speed = UnknownSpeed;
    }

    private struct Box
    {
        public Vector3 Center;
        public Vector3 Right;
        public Vector3 Forward;
        public Vector3 Half;
    }

    private readonly ManualLogSource _log;
    private readonly Action<byte, Vector3, float, float> _send;
    private readonly Dictionary<byte, Bump> _bumps = new();
    private readonly Dictionary<byte, Hit> _hits = new();
    private readonly Dictionary<byte, float> _masses = new();
    private readonly Dictionary<byte, Seen> _seen = new();
    private readonly List<byte> _done = new();
    private GameObject _boxCar;
    private Bounds _box;
    private bool _hasBox;

    public CarHits(ManualLogSource log, Action<byte, Vector3, float, float> send)
    {
        _log = log;
        _send = send;
    }

    // every physics step. parked = sat in a meet space, not where they really are, so a wall that never gets told
    public void FixedTick(RCC_CarControllerV3 car, IEnumerable<Remote> remotes, Func<Remote, bool> parked)
    {
        var body = car != null ? car.GetComponent<Rigidbody>() : null;
        if (body == null || body.isKinematic)
            return;
        if (car.gameObject != _boxCar)
        {
            _boxCar = car.gameObject;
            _hasBox = RemoteCars.TryBounds(_boxCar, out _box);
        }
        if (!_hasBox)
            return;

        var now = Time.time;
        var mine = Place(car.transform, _box.center, _box.extents);
        foreach (var remote in remotes)
        {
            var hitbox = remote.Car != null && remote.Car.activeInHierarchy ? RemoteCars.Hitbox(remote.Car) : null;
            if (hitbox == null || !hitbox.enabled)
                continue;
            var theirs = Place(hitbox.transform, hitbox.center, hitbox.size / 2f);
            var seen = Watch(remote.Id, theirs.Center, now);
            if (!Overlap(mine, theirs, Reach, out var toThem, out _))
                continue;

            if (!_bumps.TryGetValue(remote.Id, out var bump))
                _bumps[remote.Id] = bump = new Bump();
            bump.Until = now + BumpSeconds;
            var still = parked(remote);
            var wall = still || remote.IsBot;
            var velocity = still ? Vector3.zero : Believable(remote.Drive.Velocity, seen.Speed);

            if (Overlap(mine, theirs, 0f, out var normal, out var depth))
            {
                var closing = Vector3.Dot(body.velocity - velocity, normal);
                if (closing > MinClosing)
                {
                    Push(body, -normal, Impulse(closing, body.mass, Mass(remote)), bump);
                    if (!wall && now - bump.SentAt >= SendGap)
                    {
                        bump.SentAt = now;
                        _send(remote.Id, normal, closing, body.mass);
                    }
                }
                else
                {
                    Separate(body, normal, depth, velocity, wall);
                }
            }

            if (_hits.TryGetValue(remote.Id, out var hit))
            {
                _hits.Remove(remote.Id);
                var closing = Mathf.Min(hit.Closing, (seen.Speed + body.velocity.magnitude) * ClosingRatio + ClosingSlack);
                // pushed away from them, never sideways into a wall
                var away = Vector3.Dot(hit.Normal, -toThem) > MinNormalDot ? hit.Normal : -toThem;
                var push = Impulse(closing, hit.Mass, body.mass) - bump.Pushed;
                if (push > 0f)
                    Push(body, away, push, bump);
            }
        }
        Expire(now);
    }

    public static byte[] Write(byte target, Vector3 normal, float closing, float mass)
    {
        using var stream = new MemoryStream(HitBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write(target);
        writer.Write(normal.x);
        writer.Write(normal.z);
        writer.Write(closing);
        writer.Write(mass);
        return stream.ToArray();
    }

    // everyone gets every hit, only the one it's for keeps it. waits for the touch on my screen, a touch that never comes never pushes
    public void Receive(Remote from, byte myId, byte[] blob)
    {
        if (blob.Length != HitBytes || blob[0] != myId || from.Car == null)
            return;
        var normal = new Vector3(BitConverter.ToSingle(blob, 1), 0f, BitConverter.ToSingle(blob, 5));
        var closing = BitConverter.ToSingle(blob, 9);
        var mass = BitConverter.ToSingle(blob, 13);
        if (!(closing > 0f && closing < 200f) || !(mass > 50f && mass < 50000f) || !(normal.sqrMagnitude > 0.5f && normal.sqrMagnitude < 2f))
            return;
        mass = Mathf.Clamp(mass, MinMass, MaxMass);
        _masses[from.Id] = mass;
        _hits[from.Id] = new Hit { Normal = normal.normalized, Closing = closing, Mass = mass, At = Time.time };
    }

    public void Forget(byte id)
    {
        _bumps.Remove(id);
        _hits.Remove(id);
        _masses.Remove(id);
        _seen.Remove(id);
    }

    // how fast their car really moves here, measured over a tenth of a second so one jittery frame doesn't count
    private Seen Watch(byte id, Vector3 position, float now)
    {
        if (!_seen.TryGetValue(id, out var seen))
        {
            _seen[id] = seen = new Seen { Position = position, At = now };
            return seen;
        }
        var gap = now - seen.At;
        if (gap < SpeedWindow)
            return seen;
        seen.Speed = gap < 1f ? Flat(position - seen.Position).magnitude / gap : UnknownSpeed;
        seen.Position = position;
        seen.At = now;
        return seen;
    }

    private static Vector3 Believable(System.Numerics.Vector3 said, float seenSpeed) =>
        Vector3.ClampMagnitude(new Vector3(said.X, said.Y, said.Z), seenSpeed + SpeedSlack);

    private void Expire(float now)
    {
        _done.Clear();
        foreach (var pair in _bumps)
            if (now > pair.Value.Until)
                _done.Add(pair.Key);
        foreach (var id in _done)
            _bumps.Remove(id);
        _done.Clear();
        foreach (var pair in _hits)
            if (now - pair.Value.At > HoldSeconds)
                _done.Add(pair.Key);
        foreach (var id in _done)
            _hits.Remove(id);
    }

    private float Mass(Remote remote)
    {
        if (_masses.TryGetValue(remote.Id, out var mass))
            return mass;
        var body = remote.Car.GetComponent<Rigidbody>();
        return body != null && body.mass > 50f ? Mathf.Clamp(body.mass, MinMass, MaxMass) : FallbackMass;
    }

    // two cars meeting head on along the normal, a bit of bounce
    private static float Impulse(float closing, float a, float b) => (1f + Bounce) * closing * a * b / (a + b);

    // flat only, a hit never throws you up or into the road
    private void Push(Rigidbody body, Vector3 direction, float impulse, Bump bump)
    {
        direction.y = 0f;
        var kick = Mathf.Min(impulse / body.mass, MaxKick);
        body.AddForce(direction.normalized * kick, ForceMode.VelocityChange);
        bump.Pushed += impulse;
        _log.LogInfo($"Car hit: {kick:F1} m/s");
    }

    // slow and still inside each other, ease apart. half each when the other game does its half
    private static void Separate(Rigidbody body, Vector3 normal, float depth, Vector3 theirs, bool wall)
    {
        var want = Mathf.Min(depth * SeparatePerMetre, SeparateSpeed);
        var apart = Vector3.Dot(body.velocity - theirs, -normal);
        if (apart >= want)
            return;
        body.AddForce(-normal * ((want - apart) * (wall ? 1f : 0.5f)), ForceMode.VelocityChange);
    }

    private static Box Place(Transform t, Vector3 center, Vector3 half)
    {
        var right = Flat(t.right);
        var forward = Flat(t.forward);
        if (right.sqrMagnitude < 0.01f || forward.sqrMagnitude < 0.01f)
        {
            right = Vector3.right;
            forward = Vector3.forward;
        }
        return new Box { Center = t.TransformPoint(center), Right = right.normalized, Forward = forward.normalized, Half = half };
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    // seen from above, two turned rectangles plus a height check. normal points from a to b, depth is how far in
    private static bool Overlap(Box a, Box b, float grow, out Vector3 normal, out float depth)
    {
        normal = default;
        depth = float.MaxValue;
        if (Mathf.Abs(b.Center.y - a.Center.y) > a.Half.y + b.Half.y + grow)
            return false;
        var between = Flat(b.Center - a.Center);
        foreach (var axis in new[] { a.Right, a.Forward, b.Right, b.Forward })
        {
            var reach = Radius(a, axis) + Radius(b, axis) + grow;
            var distance = Vector3.Dot(between, axis);
            var overlap = reach - Mathf.Abs(distance);
            if (overlap < 0f)
                return false;
            if (overlap < depth)
            {
                depth = overlap;
                normal = distance >= 0f ? axis : -axis;
            }
        }
        return true;
    }

    private static float Radius(Box box, Vector3 axis) =>
        Mathf.Abs(Vector3.Dot(box.Right, axis)) * box.Half.x + Mathf.Abs(Vector3.Dot(box.Forward, axis)) * box.Half.z;
}
