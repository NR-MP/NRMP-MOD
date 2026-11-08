using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using HingeState = CarLocalCustom.Hinge_state;

namespace NightRunnersMP;

internal sealed class CarDoors
{
    public const KeyCode Key = KeyCode.O;
    public const byte Doors = 1;
    public const byte Hood = 2;
    public const byte Trunk = 4;
    private const byte All = Doors | Hood | Trunk;
    private const float Speed = 2f;
    private const float ShutBelow = 0.001f;
    private const float MaxShutSeconds = 4f;
    private const float StillKmh = 3f;
    private const float KeyRepeatSeconds = 0.3f;
    private const float MaxPieceOffset = 1f;

    private readonly struct Moved
    {
        public readonly Transform Part;
        public readonly Transform Parent;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public Moved(Transform part)
        {
            Part = part;
            Parent = part.parent;
            Position = part.localPosition;
            Rotation = part.localRotation;
            Scale = part.localScale;
        }

        public void Restore()
        {
            if (Part == null)
                return;
            Part.SetParent(Parent, false);
            Part.localPosition = Position;
            Part.localRotation = Rotation;
            Part.localScale = Scale;
        }
    }

    private sealed class Opened
    {
        public CarLocalCustom Car;
        public byte Applied;
        public readonly List<GameObject> Shown = new();
        public readonly List<Renderer> Hidden = new();
        public readonly List<Moved> Reattached = new();
        public bool Unmerged;
        public float ShutAt;
    }

    private readonly ManualLogSource _log;
    private readonly Opened _own = new();
    private readonly Dictionary<Remote, Opened> _remotes = new();
    private readonly HashSet<string> _failed = new();
    private bool _checked;
    private bool _allowed;
    private float _keyAt;

    public byte Open { get; private set; }

    public CarDoors(ManualLogSource log) => _log = log;

    public void Tick(RCC_CarControllerV3 car, bool blocked, IEnumerable<Remote> remotes)
    {
        if (!_checked && Rewired.ReInput.isReady)
        {
            _checked = true;
            HideUi.WarnIfGameUses(_log, Key);
        }
        var custom = car != null ? car.carLocal : null;
        _allowed = custom != null && !blocked && !car.engineRunning && Mathf.Abs(car.speed) < StillKmh;
        if (!_allowed)
            Open = 0;
        Apply(_own, custom, Open, "your car");
        foreach (var remote in remotes)
        {
            if (!_remotes.TryGetValue(remote, out var opened))
                _remotes[remote] = opened = new Opened();
            var remoteCar = remote.Car != null && remote.Car.activeSelf ? Custom(opened, remote.Car) : null;
            Apply(opened, remoteCar, remote.Hinges, remote.Name);
        }
    }

    public void OnKey(Event e)
    {
        if (e.keyCode != Key || !_allowed || Time.unscaledTime - _keyAt < KeyRepeatSeconds)
            return;
        _keyAt = Time.unscaledTime;
        var part = e.shift ? Hood : e.control ? Trunk : Doors;
        Open ^= part;
        Open &= All;
    }

    public void Forget(Remote remote) => _remotes.Remove(remote);

    private static CarLocalCustom Custom(Opened opened, GameObject car)
    {
        if (opened.Car != null && opened.Car.transform.IsChildOf(car.transform))
            return opened.Car;
        return car.GetComponentInChildren<CarLocalCustom>(true);
    }

    private void Apply(Opened opened, CarLocalCustom car, byte want, string who)
    {
        if (car != opened.Car)
        {
            opened.Car = car;
            opened.Applied = 0;
            opened.Unmerged = false;
            opened.Shown.Clear();
            opened.Hidden.Clear();
            opened.Reattached.Clear();
        }
        if (car == null)
            return;
        if (want != 0 && !opened.Unmerged)
            Unmerge(opened, who);
        var changed = (byte)(want ^ opened.Applied);
        if (changed != 0)
        {
            foreach (var part in new[] { Doors, Hood, Trunk })
                if ((changed & part) != 0)
                    Move(car, part, (want & part) != 0, who);
            opened.Applied = want;
            if (want == 0)
                opened.ShutAt = Time.unscaledTime;
        }
        if (want == 0 && opened.Unmerged && (AllShut(car) || Time.unscaledTime - opened.ShutAt > MaxShutSeconds))
            Remerge(opened, who);
    }

    private void Move(CarLocalCustom car, byte part, bool open, string who)
    {
        var state = open ? HingeState.opening : HingeState.closing;
        try
        {
            if (part == Doors)
            {
                if (car.extParent == null)
                    return;
                var parent = car.extParent.transform;
                car.door_toggleStart(parent, car.hingeDoor_FL, state, Speed, false);
                car.door_toggleStart(parent, car.hingeDoor_FR, state, Speed, false);
                if (car.hingeDoor_RL != null && car.hingeDoor_RL.hingeTransform != null)
                {
                    car.door_toggleStart(parent, car.hingeDoor_RL, state, Speed, false);
                    car.door_toggleStart(parent, car.hingeDoor_RR, state, Speed, false);
                }
            }
            else if (part == Hood)
                car.hood_toggleStart(state, false);
            else
                car.trunk_toggleStart(state, false);
        }
        catch (System.Exception e)
        {
            if (_failed.Add($"{who} {part}"))
                _log.LogWarning($"Moving {Name(part)} on {who} failed: {e.Message}");
        }
    }

    private void Unmerge(Opened opened, string who)
    {
        opened.Unmerged = true;
        var outside = Unmerge(opened, opened.Car.extParent, opened.Car.extParent != null ? opened.Car.extParent.extCombineMF : null);
        var inside = Unmerge(opened, opened.Car.intParent, opened.Car.intParent != null ? opened.Car.intParent.intCombineMF : null);
        try
        {
            Reattach(opened);
        }
        catch (System.Exception e)
        {
            if (_failed.Add($"{who} reattach"))
                _log.LogWarning($"Putting the door pieces back on {who} failed: {e.Message}");
        }
        _log.LogInfo($"Opening up {who}, {outside} outside and {inside} inside parts back on, {opened.Hidden.Count} merged meshes off, {opened.Reattached.Count} door pieces back on their doors");
    }

    // Merging moves every mesh under the combine object and the doors lose their glass and panels
    // The part's prefab still has the tree, so rebuild it like working_hinges does
    private static void Reattach(Opened opened)
    {
        var car = opened.Car;
        var loose = new Dictionary<string, List<Transform>>();
        foreach (var combine in new[] { car.extParent, car.intParent })
        {
            if (combine == null)
                continue;
            var root = combine.transform;
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.GetComponent<MeshFilter>() == null)
                    continue;
                if (!loose.TryGetValue(child.name, out var list))
                    loose[child.name] = list = new List<Transform>();
                list.Add(child);
            }
        }
        if (loose.Count == 0)
            return;
        var prefabs = new Dictionary<string, CarPart>();
        var used = new HashSet<int>();
        foreach (var part in car.GetComponentsInChildren<CarPart>(true))
        {
            if (part.door_Left == null && part.door_Right == null)
                continue;
            var prefab = Prefab(part, prefabs);
            if (prefab == null)
                continue;
            Rebuild(opened, prefab.door_Left, part.door_Left, loose, used);
            Rebuild(opened, prefab.door_Right, part.door_Right, loose, used);
        }
    }

    private static void Rebuild(Opened opened, Transform prefabRoot, Transform liveRoot, Dictionary<string, List<Transform>> loose, HashSet<int> used)
    {
        if (prefabRoot == null || liveRoot == null)
            return;
        var live = new Dictionary<int, Transform> { [prefabRoot.GetInstanceID()] = liveRoot };
        foreach (var piece in prefabRoot.GetComponentsInChildren<Transform>(true))
        {
            if (piece.GetInstanceID() == prefabRoot.GetInstanceID() || !live.TryGetValue(piece.parent.GetInstanceID(), out var parent))
                continue;
            var found = Child(parent, piece.name, used);
            if (found == null && piece.GetComponent<MeshFilter>() != null && loose.TryGetValue(piece.name, out var candidates))
            {
                found = Nearest(candidates, parent.TransformPoint(piece.localPosition), used);
                if (found != null)
                {
                    opened.Reattached.Add(new Moved(found));
                    found.SetParent(parent, false);
                    found.localPosition = piece.localPosition;
                    found.localRotation = piece.localRotation;
                    found.localScale = piece.localScale;
                }
            }
            if (found == null)
                continue;
            used.Add(found.GetInstanceID());
            live[piece.GetInstanceID()] = found;
        }
    }

    private static Transform Child(Transform parent, string name, HashSet<int> used)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name && !used.Contains(child.GetInstanceID()))
                return child;
        }
        return null;
    }

    private static Transform Nearest(List<Transform> candidates, Vector3 at, HashSet<int> used)
    {
        Transform best = null;
        var bestDistance = MaxPieceOffset;
        foreach (var candidate in candidates)
        {
            if (candidate == null || used.Contains(candidate.GetInstanceID()))
                continue;
            var distance = Vector3.Distance(candidate.position, at);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static CarPart Prefab(CarPart part, Dictionary<string, CarPart> cache)
    {
        if (part.carPart_original != null && !part.carPart_original.gameObject.scene.IsValid())
            return part.carPart_original;
        if (cache.Count == 0)
            foreach (var asset in Resources.FindObjectsOfTypeAll<CarPart>())
                if (asset != null && !asset.gameObject.scene.IsValid())
                    cache.TryAdd($"{(int)asset.partType} {asset.name}", asset);
        var name = part.name.Replace("(Clone)", "").Trim();
        return cache.TryGetValue($"{(int)part.partType} {name}", out var prefab) ? prefab : null;
    }

    private static int Unmerge(Opened opened, CarMeshCombine combine, Il2CppSystem.Collections.Generic.List<MeshFilter> filters)
    {
        if (combine == null || filters == null)
            return 0;
        var shown = 0;
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (filter == null || filter.gameObject.activeSelf)
                continue;
            filter.gameObject.SetActive(true);
            opened.Shown.Add(filter.gameObject);
            shown++;
        }
        if (shown > 0)
        {
            Hide(opened, combine.localMeshRenderer);
            Hide(opened, combine.GetComponent<MeshRenderer>());
        }
        return shown;
    }

    private static void Hide(Opened opened, Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || opened.Hidden.Contains(renderer))
            return;
        renderer.enabled = false;
        opened.Hidden.Add(renderer);
    }

    private void Remerge(Opened opened, string who)
    {
        for (var i = opened.Reattached.Count - 1; i >= 0; i--)
            opened.Reattached[i].Restore();
        opened.Reattached.Clear();
        foreach (var part in opened.Shown)
            if (part != null)
                part.SetActive(false);
        foreach (var renderer in opened.Hidden)
            if (renderer != null)
                renderer.enabled = true;
        _log.LogInfo($"{who} all shut, merged again");
        opened.Shown.Clear();
        opened.Hidden.Clear();
        opened.Unmerged = false;
    }

    private static bool AllShut(CarLocalCustom car) =>
        Shut(car.hingeDoor_FL) && Shut(car.hingeDoor_FR) && Shut(car.hingeDoor_RL) && Shut(car.hingeDoor_RR)
        && Shut(car.hingeHood) && Shut(car.hingeTrunk);

    private static bool Shut(CarLocalCustom.hinge_car hinge) => hinge == null || hinge.hinge_timer <= ShutBelow;

    private static string Name(byte part) => part == Doors ? "the doors" : part == Hood ? "the hood" : "the trunk";
}
