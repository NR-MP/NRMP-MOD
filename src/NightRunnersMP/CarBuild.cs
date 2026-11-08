using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using PlanetJem.Customization;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace NightRunnersMP;

// car parts as bytes, from the save via CarConfigRepository. server just stores and forwards.
// no engine or handling parts, you can't see those on a car that's only drawn
internal static class CarBuild
{
    private const int FullBodykit = 34;
    private const int LastWheel = 43;
    private const int NeonExterior = 70;

    public static byte[] Read(int saveId)
    {
        if (!CarConfigRepository.TryRead(saveId, out var config) || config == null)
            return null;
        var w = new NetDataWriter();
        var parts = new List<PartRef>();
        for (var i = 0; i < config.Parts.Count; i++)
            if (IsVisible(config.Parts[i]))
                parts.Add(config.Parts[i]);
        // kit first, fitting one removes what it replaces
        parts.Sort((a, b) => IsKit(b).CompareTo(IsKit(a)));
        w.Put((ushort)parts.Count);
        foreach (var part in parts)
        {
            w.Put((int)part.PartType);
            w.Put(part.Index);
            w.Put((byte)part.IndexKind);
            w.Put((int)part.Chassis);
            w.Put(part.IsFrontEndSwap);
        }

        var subs = config.SubParts;
        w.Put((ushort)(subs != null ? subs.Count : 0));
        for (var i = 0; subs != null && i < subs.Count; i++)
        {
            w.Put((int)subs[i].PartType);
            w.Put((ushort)subs[i].Enabled.Count);
            for (var e = 0; e < subs[i].Enabled.Count; e++)
                w.Put(subs[i].Enabled[e]);
        }

        var paint = config.Paint;
        w.Put(paint != null ? paint.NeonExteriorColorIndex : -1);
        w.Put(paint != null ? paint.NeonInteriorColorIndex : -1);

        var stance = config.Stance;
        var fit = stance?.Suspension;
        w.Put(fit != null);
        if (fit != null)
        {
            w.Put(fit.rideHeightLevel);
            w.Put(fit.rideHeightRake);
            w.Put(fit.frontFitmentLevel);
            w.Put(fit.rearFitmentLevel);
            w.Put(fit.frontRimSize);
            w.Put(fit.rearRimSize);
            w.Put(fit.frontWheelThickness);
            w.Put(fit.rearWheelThickness);
            w.Put(fit.frontTireProfile);
            w.Put(fit.rearTireProfile);
            w.Put((int)fit.front_TireSidewallType);
            w.Put((int)fit.rear_TireSidewallType);
            w.Put(fit.front_TireProfileIndex);
            w.Put(fit.rear_TireProfileIndex);
            w.Put(fit.front_TireMeshIndex);
            w.Put(fit.rear_TireMeshIndex);
            w.Put(stance.CamberFront);
            w.Put(stance.CamberRear);
        }

        var main = paint != null && paint.LiveryEquipped ? LiverySync.Keep(paint.LiveryPath) : null;
        w.Put(main != null);
        if (main != null)
        {
            var mask = LiverySync.Keep(paint.LiveryMaskPath);
            w.Put((int)paint.LiveryMaterial);
            w.Put(Convert.FromHexString(main));
            w.Put(mask != null);
            if (mask != null)
                w.Put(Convert.FromHexString(mask));
        }

        // the swap decides which catalog the front parts' numbers mean anything in
        var body = config.Body;
        w.Put(body != null && body.HasFrontEndSwap);
        if (body != null && body.HasFrontEndSwap)
            w.Put((int)body.FrontEndSwapChassis);
        return w.CopyData();
    }

    public static Job Parse(byte[] build)
    {
        var r = new NetDataReader(build);
        var job = new Job();
        for (var n = r.GetUShort(); n > 0; n--)
        {
            job.Parts.Add(new PartRef
            {
                PartType = (CarPart.PartType)r.GetInt(),
                Index = r.GetInt(),
                IndexKind = (PartIndexKind)r.GetByte(),
                Chassis = (car_carOrigin.ChassisType)r.GetInt(),
                IsFrontEndSwap = r.GetBool(),
            });
        }
        for (var n = r.GetUShort(); n > 0; n--)
        {
            var sub = new SubPartState
            {
                PartType = (CarPart.PartType)r.GetInt(),
                Enabled = new Il2CppSystem.Collections.Generic.List<bool>(),
            };
            for (var e = r.GetUShort(); e > 0; e--)
                sub.Enabled.Add(r.GetBool());
            job.SubParts.Add(sub);
        }
        job.NeonExterior = r.GetInt();
        job.NeonInterior = r.GetInt();
        if (r.GetBool())
        {
            var fit = new CarLocalCustom.WheelSuspension
            {
                rideHeightLevel = r.GetFloat(),
                rideHeightRake = r.GetFloat(),
                frontFitmentLevel = r.GetFloat(),
                rearFitmentLevel = r.GetFloat(),
                frontRimSize = r.GetFloat(),
                rearRimSize = r.GetFloat(),
                frontWheelThickness = r.GetFloat(),
                rearWheelThickness = r.GetFloat(),
                frontTireProfile = r.GetFloat(),
                rearTireProfile = r.GetFloat(),
                front_TireSidewallType = (CarLocalCustom.TireSidewallType)r.GetInt(),
                rear_TireSidewallType = (CarLocalCustom.TireSidewallType)r.GetInt(),
                front_TireProfileIndex = r.GetInt(),
                rear_TireProfileIndex = r.GetInt(),
                front_TireMeshIndex = r.GetInt(),
                rear_TireMeshIndex = r.GetInt(),
            };
            job.Stance = new StanceSet { Suspension = fit, CamberFront = r.GetFloat(), CamberRear = r.GetFloat() };
        }
        // hex of raw bytes off the wire, so a hash can't turn into a path
        if (r.AvailableBytes > 0 && r.GetBool())
        {
            job.LiveryFinish = (LiveryMaterial)r.GetInt();
            job.LiveryMain = ReadHash(r);
            if (r.GetBool())
                job.LiveryMask = ReadHash(r);
        }
        if (r.AvailableBytes > 0 && r.GetBool())
            job.FrontEnd = (car_carOrigin.ChassisType)r.GetInt();
        return job;
    }

    private static string ReadHash(NetDataReader r)
    {
        var bytes = new byte[Protocol.LiveryHashBytes];
        r.GetBytes(bytes, bytes.Length);
        return Convert.ToHexString(bytes);
    }

    private static bool IsKit(PartRef part) => (int)part.PartType == FullBodykit;

    private static bool IsVisible(PartRef part)
    {
        var type = (int)part.PartType;
        return type <= LastWheel || type == NeonExterior;
    }

    internal sealed class Job
    {
        public readonly List<PartRef> Parts = new();
        public readonly List<SubPartState> SubParts = new();
        public int NeonExterior;
        public int NeonInterior;
        public StanceSet Stance;
        public string LiveryMain;
        public string LiveryMask;
        public LiveryMaterial LiveryFinish;
        public car_carOrigin.ChassisType? FrontEnd;
    }
}

// fits remote parts on the stock car the way the parts shop does, one per frame so big builds don't hitch.
// part won't fit = skip it, stock stays
internal sealed class RemoteBuilds
{
    public static bool Enabled;
    private const float CatalogTimeoutSeconds = 15f;
    private const int FrontEndParts = 4;
    private const float WatchSeconds = 1f;
    private const float MinWhole = 0.8f;
    private const int MaxRebuilds = 2;
    // a car that stayed whole this long after its last rebuild lost parts to something new (garage, loading screen), not a loop
    private const float RebuildFreshSeconds = 60f;
    private static readonly HashSet<string> HeldCatalogs = new();

    private readonly ManualLogSource _log;
    private readonly Queue<Remote> _waiting = new();
    private Remote _remote;
    private GameObject _car;
    private CarBuild.Job _job;
    private int _step;
    private int _fitted;
    private int _skipped;
    // skipped parts a new build can't fix, like the interior on a mesh-combined car
    private int _neverFit;
    private CarParent _parent;
    private CarParent._load_customization_d__82 _catalog;
    private bool _catalogAsked;
    private bool _catalogDone;
    private float _catalogDeadline;
    private GameObject _extRoot;
    private GameObject _frontRoot;
    private GameObject _wheelRoot;
    private GameObject _ownExt;
    private GameObject _ownFront;
    private GameObject _ownWheel;
    private float _nextWatch;
    private int _watched;
    private bool _ownWheelsLogged;

    public Func<bool> Spawning;
    // spawning and fitting share CarParent's fields and overlapping them left cars bare on some screens, so it's one car start to finish
    public bool Busy => _remote != null || _waiting.Count > 0;

    public RemoteBuilds(ManualLogSource log) => _log = log;

    public void Start(Remote remote) => _waiting.Enqueue(remote);

    public bool IsFitting(Remote remote) => remote == _remote || _waiting.Contains(remote);

    public void Tick(bool hold)
    {
        // waiting in the garage isn't the catalog being slow
        if (hold)
        {
            _catalogDeadline += Time.unscaledDeltaTime;
            return;
        }
        if (_remote != null && _car != null && _remote.Car == _car)
            Step();
        else
            Next();
    }

    private void Next()
    {
        _remote = null;
        if (_waiting.Count == 0 || Spawning())
            return;
        var remote = _waiting.Dequeue();
        if (remote.Car == null || remote.Build == null)
            return;
        try
        {
            _job = CarBuild.Parse(remote.Build);
        }
        catch (Exception e)
        {
            _log.LogWarning($"Unreadable build from {remote.Name}: {e.Message}");
            return;
        }
        _remote = remote;
        _car = remote.Car;
        _step = 0;
        _fitted = 0;
        _skipped = 0;
        _neverFit = 0;
        _catalog = null;
        _catalogAsked = false;
        _catalogDone = false;
        _extRoot = _frontRoot = _wheelRoot = null;
        var list = new System.Text.StringBuilder();
        foreach (var part in _job.Parts)
            list.Append($" {part.PartType}:{part.Index}:{part.Chassis}{(part.IsFrontEndSwap ? ":swap" : "")}");
        var front = _job.FrontEnd.HasValue ? _job.FrontEnd.Value.ToString() : "default";
        _log.LogInfo($"{remote.Name}'s build, front end {front}:{list}");
    }

    private void Step()
    {
        var car = _remote.Controller != null ? _remote.Controller.carLocal : null;
        if (car == null)
        {
            _remote = null;
            return;
        }
        if (!_catalogDone)
        {
            WaitForCatalog(car);
            return;
        }
        var parts = _job.Parts.Count;
        var start = SlowStep.Start();
        var name = _remote.Name;
        if (_step < parts)
        {
            Fit(car, _job.Parts[_step]);
            SlowStep.Stop(start, $"fitting {_job.Parts[_step].PartType} on {name}'s car");
        }
        else if (_step < parts + _job.SubParts.Count)
        {
            FitSubParts(car, _job.SubParts[_step - parts]);
            SlowStep.Stop(start, $"sub parts on {name}'s car");
        }
        else
        {
            Finish(car);
            SlowStep.Stop(start, $"finishing {name}'s car");
        }
        _step++;
    }

    // the catalogs sit in shared fields that every npc car load overwrites, and the spawn only ever loads the first front end.
    // so load this car's own set, keep hold of them and lend them back for the frame each part is fitted
    private void WaitForCatalog(CarLocalCustom car)
    {
        if (!_catalogAsked)
        {
            _parent = UnityEngine.Object.FindObjectOfType<CarParent>();
            // a spawn fills the same fields, one finishing during this load hands me its car's catalogs
            if (_parent == null || _parent.loading_custom || Spawning())
                return;
            _catalogAsked = true;
            _catalogDeadline = Time.unscaledTime + CatalogTimeoutSeconds;
            try
            {
                var origin = car.carOrigin;
                var swaps = origin.carOrigin_frontEndSwaps;
                var front = _job.FrontEnd ?? (swaps.Count > 0 ? swaps[0].frontEndSwap_chassisType : default);
                HoldCatalogs(origin, front);
                _ownExt = _parent.extCustom;
                _ownFront = _parent.extFrontCustom;
                _ownWheel = _parent.whlCustom;
                var routine = _parent.load_customization(origin, front, origin.carOrigin_engines[0].engineType, CarParent.ParentType._all);
                _catalog = routine.TryCast<CarParent._load_customization_d__82>();
                var start = SlowStep.Start();
                _parent.StartCoroutine(routine);
                SlowStep.Stop(start, $"loading the part catalogs for {_remote.Name}'s car");
            }
            catch (Exception e)
            {
                _log.LogWarning($"{_remote.Name}: couldn't load the catalogs: {e.Message}");
                _catalogDone = true;
            }
            return;
        }

        // scene changed under it, ask again
        if (_parent == null)
        {
            _catalogAsked = false;
            return;
        }
        var running = _catalog != null && _catalog.__1__state >= 0;
        if ((running || _parent.loading_custom) && Time.unscaledTime < _catalogDeadline)
            return;
        _catalogDone = true;
        if (running || _parent.loading_custom)
        {
            _log.LogWarning($"{_remote.Name}: catalogs still loading, fitting from whatever's there");
            return;
        }
        _extRoot = _parent.extCustom;
        _frontRoot = _parent.extFrontCustom;
        _wheelRoot = _parent.whlCustom;
        _log.LogInfo($"{_remote.Name}: catalogs {Named(_extRoot)}, {Named(_frontRoot)}, {Named(_wheelRoot)}");
        // the garage reads these too, they were never mine to leave changed
        if (_parent.extCustom == _extRoot)
        {
            _parent.extCustom = _ownExt;
            _parent.extFrontCustom = _ownFront;
            _parent.whlCustom = _ownWheel;
        }
    }

    // every loading screen and race countdown the game lets go of all catalogs but the player's, and the meshes on a fitted part
    // go with them. one extra load per catalog that nothing ever releases keeps them in
    private void HoldCatalogs(car_carOrigin origin, car_carOrigin.ChassisType front)
    {
        try
        {
            Hold(origin.carOrigin_exteriorCustomization);
            Hold(_parent.carOrigin_wheelCustomization);
            var swaps = origin.carOrigin_frontEndSwaps;
            for (var i = 0; i < swaps.Count; i++)
                if (swaps[i].frontEndSwap_chassisType == front)
                    Hold(swaps[i].carOrigin_frontEndCustomization);
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: couldn't hold the catalogs: {e.Message}");
        }
    }

    private void Hold(AssetReference catalog)
    {
        if (catalog == null || string.IsNullOrEmpty(catalog.AssetGUID) || !HeldCatalogs.Add(catalog.AssetGUID))
            return;
        Addressables.LoadAssetAsync<GameObject>(catalog.RuntimeKey);
        _log.LogInfo($"Holding catalog {catalog.AssetGUID}, {HeldCatalogs.Count} held");
    }

    // a car only changes when its owner sends a new build, so one that lost parts here gets built again from the same build
    public void Watch(ICollection<Remote> remotes, Predicate<Remote> inUse)
    {
        if (Busy || remotes.Count == 0 || Time.unscaledTime < _nextWatch)
            return;
        _nextWatch = Time.unscaledTime + WatchSeconds;
        var remote = remotes.ElementAt(_watched++ % remotes.Count);
        LogOwnWheels();
        if (remote.Car == null || remote.Fitted == 0)
            return;
        var wheelsGone = CheckWheels(remote);
        if (inUse(remote))
            return;
        var whole = Whole(remote.Car);
        if (whole >= remote.Fitted * MinWhole && !wheelsGone)
            return;
        if (remote.Rebuilds > 0 && Time.unscaledTime - remote.RebuiltAt > RebuildFreshSeconds)
            remote.Rebuilds = 0;
        var again = remote.Rebuilds < MaxRebuilds;
        _log.LogWarning($"{remote.Name}'s car lost parts, {whole} of {remote.Fitted} left, {(again ? "building it again" : "giving up")}");
        remote.Fitted = 0;
        if (!again)
            return;
        remote.Rebuilds++;
        remote.RebuiltAt = Time.unscaledTime;
        remote.BuildOn = null;
    }

    // wheels are a sliver of a car's meshes so the count above never sees them go, they get checked on their own
    private bool CheckWheels(Remote remote)
    {
        var state = WheelState(remote.Car, out var gone, out var detail);
        if (state == remote.WheelsSeen)
            return gone > 0;
        remote.WheelsSeen = state;
        _log.LogInfo($"{remote.Name}'s wheels now {state}:{detail}");
        return gone > 0;
    }

    // what a working wheel looks like, to hold the others against
    private void LogOwnWheels()
    {
        var own = NetBehaviour.LocalCar;
        if (_ownWheelsLogged || own == null)
            return;
        _ownWheelsLogged = true;
        var state = WheelState(own.gameObject, out _, out var detail);
        _log.LogInfo($"My wheels {state}:{detail}");
        _log.LogInfo($"My wheel chain:{WheelChain(own.gameObject)}");
        _log.LogInfo($"My wheel drawn:{WheelDrawn(own.gameObject)}");
    }

    // everything under one wheel including what isn't a part, a stock rim left drawing doesn't show up in the part lists
    private static string WheelDrawn(GameObject car)
    {
        var list = new System.Text.StringBuilder();
        var model = car.GetComponent<RCC_CarControllerV3>()?.FrontLeftWheelCollider?.wheelModel;
        if (model == null)
            return " no wheel";
        foreach (var mesh in model.GetComponentsInChildren<Renderer>(true))
        {
            var filter = mesh.GetComponent<MeshFilter>();
            var mats = new System.Text.StringBuilder();
            foreach (var mat in mesh.sharedMaterials)
                mats.Append(mat != null ? mat.name + "," : "none,");
            list.Append($" {mesh.transform.parent?.name}/{mesh.name} mesh {(filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "none")} on {mesh.enabled}/{mesh.gameObject.activeInHierarchy} mats {mats};");
        }
        foreach (var lod in car.GetComponentsInChildren<LODGroup>(true))
            list.Append($" lod {lod.name} on {lod.enabled};");
        return list.ToString();
    }

    // every parent from the rim up with its own position and scale, a wheel 100 times too small sits under the wrong one of these
    private static string WheelChain(GameObject car)
    {
        var list = new System.Text.StringBuilder();
        foreach (var part in car.GetComponentsInChildren<CarPart>(true))
        {
            if (part.partType != CarPart.PartType.whl_rimFront && part.partType != CarPart.PartType.whl_rimRear)
                continue;
            list.Append(" rim");
            for (var at = part.transform; at != null && at != car.transform; at = at.parent)
                list.Append($" < {at.name} {at.localPosition} {at.localScale}");
            list.Append(';');
        }
        var controller = car.GetComponent<RCC_CarControllerV3>();
        var model = controller?.FrontLeftWheelCollider?.wheelModel;
        if (model != null)
        {
            list.Append(" model");
            for (var at = model; at != null && at != car.transform; at = at.parent)
                list.Append($" < {at.name} {at.localPosition} {at.localScale}");
        }
        return list.ToString();
    }

    private static string WheelState(GameObject car, out int gone, out string detail)
    {
        var root = car.transform;
        var total = 0;
        var off = 0;
        gone = 0;
        var list = new System.Text.StringBuilder();
        foreach (var part in car.GetComponentsInChildren<CarPart>(true))
        {
            var type = part.partType.ToString();
            if (!type.StartsWith("whl_rim") && !type.StartsWith("whl_tire"))
                continue;
            foreach (var mesh in part.GetComponentsInChildren<MeshRenderer>(true))
            {
                total++;
                var filter = mesh.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || mesh.sharedMaterial == null)
                {
                    gone++;
                    list.Append($" {type} {mesh.name} no mesh or material;");
                    continue;
                }
                if (!mesh.enabled || !mesh.gameObject.activeInHierarchy)
                    off++;
                var shape = mesh.bounds;
                if (total <= 2)
                    list.Append($" {type} {mesh.name} on {mesh.enabled}/{mesh.gameObject.activeInHierarchy} at {root.InverseTransformPoint(shape.center)} size {shape.size} scale {mesh.transform.lossyScale} layer {mesh.gameObject.layer} shader {mesh.sharedMaterial.shader?.name} seen {mesh.isVisible};");
            }
        }
        detail = list.ToString();
        return $"{total} meshes, {gone} gone, {off} off";
    }

    private static int Whole(GameObject car)
    {
        var whole = 0;
        foreach (var mesh in car.GetComponentsInChildren<MeshRenderer>(true))
        {
            var filter = mesh.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null && mesh.sharedMaterial != null)
                whole++;
        }
        return whole;
    }

    // a mesh or material gone from a part means its catalog got let go after fitting
    public static int Broken(GameObject car)
    {
        var broken = 0;
        foreach (var mesh in car.GetComponentsInChildren<MeshRenderer>(true))
        {
            var filter = mesh.GetComponent<MeshFilter>();
            if (mesh.enabled && (filter == null || filter.sharedMesh == null || mesh.sharedMaterial == null))
                broken++;
        }
        return broken;
    }

    private static string Named(GameObject root) => root != null ? root.name : "none";

    private void Fit(CarLocalCustom car, PartRef part)
    {
        var parent = _parent;
        var lend = parent != null && _extRoot != null;
        var ext = lend ? parent.extCustom : null;
        var front = lend ? parent.extFrontCustom : null;
        var wheel = lend ? parent.whlCustom : null;
        try
        {
            if (lend)
            {
                parent.extCustom = _extRoot;
                parent.extFrontCustom = _frontRoot;
                parent.whlCustom = _wheelRoot;
            }
            PrepareTire(car, part);
            var result = new CarApplyResult();
            if (CarApplicator.TryApplyPart(car, part, result))
            {
                _fitted++;
                return;
            }
            _log.LogInfo($"{_remote.Name}: no {part.PartType} {part.Index}: {result.Error}");
            // the game says this one itself, the car's interior is one merged mesh so it never takes these 
            if (result.Error != null && result.Error.Contains("must be uncombined"))
                _neverFit++;
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: {part.PartType} {part.Index} failed: {e.Message}");
        }
        finally
        {
            if (lend)
            {
                parent.extCustom = ext;
                parent.extFrontCustom = front;
                parent.whlCustom = wheel;
            }
        }
        _skipped++;
    }

    private void PrepareTire(CarLocalCustom car, PartRef part)
    {
        var fit = _job.Stance?.Suspension;
        var own = fit != null ? car.wheelSuspension : null;
        if (own == null)
            return;
        if (part.PartType == CarPart.PartType.whl_tireFront && fit.front_TireProfileIndex >= 0)
        {
            own.front_TireSidewallType = fit.front_TireSidewallType;
            own.front_TireProfileIndex = fit.front_TireProfileIndex;
            own.front_TireMeshIndex = fit.front_TireMeshIndex;
        }
        else if (part.PartType == CarPart.PartType.whl_tireRear && fit.rear_TireProfileIndex >= 0)
        {
            own.rear_TireSidewallType = fit.rear_TireSidewallType;
            own.rear_TireProfileIndex = fit.rear_TireProfileIndex;
            own.rear_TireMeshIndex = fit.rear_TireMeshIndex;
        }
    }

    private void KeepTiresDrawn(CarLocalCustom car)
    {
        var shown = 0;
        foreach (var part in car.GetComponentsInChildren<CarPart>(false))
        {
            if (part.partType != CarPart.PartType.whl_tireFront && part.partType != CarPart.PartType.whl_tireRear)
                continue;
            var meshes = part.GetComponentsInChildren<MeshRenderer>(true);
            if (meshes.Length == 0 || meshes.Any(m => m.gameObject.activeInHierarchy))
                continue;
            meshes[0].gameObject.SetActive(true);
            shown++;
        }
        if (shown > 0)
            _log.LogWarning($"{_remote.Name}: {shown} tires had no profile mesh left, showing the stock one");
    }

    private void FitSubParts(CarLocalCustom car, SubPartState sub)
    {
        try
        {
            CarApplicator.TryApplySubParts(car, sub, new CarApplyResult());
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: sub parts of {sub.PartType} failed: {e.Message}");
        }
    }

    // new parts bring colliders and sounds, a ghost can't have those
    private void Finish(CarLocalCustom car)
    {
        ApplyStance(car);
        try
        {
            car.car_updateNeonColor(true, _job.NeonExterior, _job.NeonInterior);
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: neon failed: {e.Message}");
        }
        HideRemovedParts(car);
        RemoteCars.MakeGhost(_car);
        HideMergedWheels();
        _remote.NextRest = 0f;
        _remote.PaintedCar = null;
        _remote.Fitted = Whole(_car);
        _remote.WheelsSeen = null;
        _log.LogInfo($"Fitted {_fitted} parts to {_remote.Name}'s car, {_skipped} skipped, {_remote.Fitted} meshes, {Broken(_car)} broken");
        LogShell();
        LogWheels();
        // a part that didnt fit leaves the old panel or a hole until the owner sends a new build, does a full fresh spawn and fit currently lags a bit
        if (_skipped > _neverFit && _remote.Rebuilds < MaxRebuilds)
        {
            _log.LogWarning($"{_remote.Name}'s car skipped {_skipped - _neverFit} parts that could fit, building it again");
            _remote.Rebuilds++;
            _remote.RebuiltAt = Time.unscaledTime;
            _remote.BuildOn = null;
        }
        _remote = null;
    }

    private void ApplyStance(CarLocalCustom car)
    {
        if (_job.Stance == null)
            return;
        try
        {
            CarApplicator.TryApplyStance(car, _job.Stance, new CarApplyResult());
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: stance failed: {e.Message}");
            return;
        }
        var fit = _job.Stance.Suspension;
        var front = fit != null && fit.front_TireProfileIndex >= 0;
        var rear = fit != null && fit.rear_TireProfileIndex >= 0;
        if (!front && !rear)
            return;
        try
        {
            WheelRimFitment.ApplyRimVisuals(car, front, rear);
            KeepTiresDrawn(car);
            _log.LogInfo($"{_remote.Name}: tire profile {fit.front_TireProfileIndex}/{fit.rear_TireProfileIndex}, sidewall {fit.front_TireSidewallType}/{fit.rear_TireSidewallType}");
        }
        catch (Exception e)
        {
            _log.LogWarning($"{_remote.Name}: tire profile failed: {e.Message}");
        }
    }

    private static readonly CarPart.PartType[] Removable =
    {
        CarPart.PartType.ext_front_bumper, CarPart.PartType.ext_rear_bumper, CarPart.PartType.ext_sideskirt,
        CarPart.PartType.ext_hood, CarPart.PartType.ext_mirrors, CarPart.PartType.ext_spoiler,
    };

    private void HideRemovedParts(CarLocalCustom car)
    {
        if (_job.Parts.Any(p => p.PartType == CarPart.PartType.ext_fullBodykit))
            return;
        foreach (var type in Removable)
        {
            if (_job.Parts.Any(p => p.PartType == type))
                continue;
            try
            {
                var part = CarApplicator.FindInstalledCarPartPublic(car, type);
                if (part == null || !part.gameObject.activeSelf)
                    continue;
                part.gameObject.SetActive(false);
                _log.LogInfo($"{_remote.Name} removed their {type}, hid it");
            }
            catch (Exception e)
            {
                _log.LogWarning($"{_remote.Name}: hiding {type} failed: {e.Message}");
            }
        }
    }

    // the spawn merges the stock wheel into one mesh on the hub and it keeps drawing around the fitted rim. a hub with no rim on it keeps its mesh
    private void HideMergedWheels()
    {
        var controller = _car.GetComponent<RCC_CarControllerV3>();
        if (controller == null)
            return;
        var hidden = 0;
        foreach (var wheel in new[] { controller.FrontLeftWheelCollider, controller.FrontRightWheelCollider, controller.RearLeftWheelCollider, controller.RearRightWheelCollider })
        {
            var model = wheel != null ? wheel.wheelModel : null;
            if (model == null)
                continue;
            foreach (var hub in model.GetComponentsInChildren<CarMeshCombine>(true))
            {
                var merged = hub.GetComponent<MeshRenderer>();
                if (merged == null || !merged.enabled || !HasRim(hub))
                    continue;
                merged.enabled = false;
                hidden++;
            }
        }
        _log.LogInfo($"Hid {hidden} merged wheels on {_remote.Name}'s car");
    }

    private static bool HasRim(CarMeshCombine hub)
    {
        foreach (var part in hub.GetComponentsInChildren<CarPart>(false))
            if (part.partType == CarPart.PartType.whl_rimFront || part.partType == CarPart.PartType.whl_rimRear)
                return true;
        return false;
    }

    private void LogWheels()
    {
        var list = new System.Text.StringBuilder();
        var fit = _job.Stance?.Suspension;
        if (fit != null)
            list.Append($" rims {fit.frontRimSize}/{fit.rearRimSize} width {fit.frontWheelThickness}/{fit.rearWheelThickness} tires {fit.frontTireProfile}/{fit.rearTireProfile}");
        foreach (var part in _car.GetComponentsInChildren<CarPart>(true))
        {
            if (part.name.IndexOf("rim", StringComparison.OrdinalIgnoreCase) < 0 && part.name.IndexOf("tire", StringComparison.OrdinalIgnoreCase) < 0
                && !part.partType.ToString().StartsWith("whl_"))
                continue;
            var drawn = 0;
            foreach (var mesh in part.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = mesh.GetComponent<MeshFilter>();
                if (mesh.enabled && filter != null && filter.sharedMesh != null)
                    drawn++;
            }
            list.Append($" {part.partType}={part.name}:{(part.gameObject.activeInHierarchy ? "on" : "off")}:{drawn}:{part.transform.lossyScale.y:0.##}");
        }
        _log.LogInfo($"{_remote.Name}'s wheels:{list}");
        _log.LogInfo($"{_remote.Name}'s wheel chain:{WheelChain(_car)}");
        _log.LogInfo($"{_remote.Name}'s wheel drawn:{WheelDrawn(_car)}");
        foreach (var sub in _job.SubParts)
            if (sub.PartType.ToString().StartsWith("whl_"))
                _log.LogInfo($"{_remote.Name}'s {sub.PartType} sub parts: {string.Join(",", sub.Enabled.ToArray())}");
    }

    // says what actually ended up on the car, a part can fit fine and still draw nothing
    private void LogShell()
    {
        var list = new System.Text.StringBuilder();
        foreach (var part in _car.GetComponentsInChildren<CarPart>(true))
        {
            if ((int)part.partType > FrontEndParts)
                continue;
            var drawn = 0;
            foreach (var mesh in part.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = mesh.GetComponent<MeshFilter>();
                if (mesh.enabled && filter != null && filter.sharedMesh != null)
                    drawn++;
            }
            list.Append($" {part.partType}={part.name}:{(part.gameObject.activeInHierarchy ? "on" : "off")}:{drawn}");
        }
        _log.LogInfo($"{_remote.Name}'s front:{list}");
    }
}
