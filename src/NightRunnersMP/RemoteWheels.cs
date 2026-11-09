using System;
using System.Collections.Generic;
using NightRunnersMP.Shared;
using UnityEngine;

namespace NightRunnersMP;

internal sealed class WheelVisual
{
    public RCC_WheelCollider Wheel;
    public Transform Model;
    public Transform Mesh;
    public Quaternion Rest;
    public bool Right;
    public float Drop = -1f;
    public Transform Brake;
    public Quaternion BrakeRest;
    public bool Front;
    public float Spin;
    public float Steer;
    public int Chain = -1;
    public float MarkWidth;
    public RCC_Skidmarks Marks;
    public readonly List<ParticleSystem> Smoke = new();
    public readonly List<float> SmokeRates = new();
    public bool SmokeMade;
    public bool SmokeOn;
    public float SmokeAge;
    public int Ground;
}

// ghosts have their wheel colliders off so the game skips its wheel update. same trick as its replay player: the numbers come off the
// real car, i put them on the wheel model, the skidmark mesh and the wheel's own smoke
internal sealed class RemoteWheels
{
    private const float RangeMetres = 250f;
    private const float SteerPerSecond = 300f;
    private const float DegreesPerRpmSecond = 6f;
    private const float MinSkid = 0.1f;
    private const float RayLift = 0.5f;
    private const float RayLength = 2f;
    private const float DefaultMarkWidth = 0.25f;
    // RCC_WheelCollider.WheelAlign clamps visual camber to 10 either way (MaxVisualCamberDegrees)
    private const float MaxCamber = 10f;
    private const float DropPerSecond = 2f;
    // RCC_WheelCollider.Frictions, thin wisps first and the real cloud only after a held burnout
    private const float SmokeBuildAfter = 2.25f;
    private const float SmokeBuildSeconds = 4f;
    private const float SmokeThin = 0.12f;
    private const float SmokeSizeScale = 0.7f;
    private const float SmokeSizeGrowth = 0.88f;
    private const float SmokeRateThin = 0.15f;
    private const float SmokeRateGrowth = 0.85f;
    private const float SmokeLifeSeconds = 2f;

    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> Hits = new(16);

    private readonly RemoteBuilds _builds;

    public RemoteWheels(RemoteBuilds builds) => _builds = builds;

    public static void Fill(ref CarState state, RCC_CarControllerV3 car)
    {
        var fl = car.FrontLeftWheelCollider;
        var fr = car.FrontRightWheelCollider;
        var rl = car.RearLeftWheelCollider;
        var rr = car.RearRightWheelCollider;
        if (fl == null || fr == null || rl == null || rr == null)
            return;
        state.Steer = (sbyte)Mathf.Clamp(fl.wheelCollider.steerAngle, sbyte.MinValue, sbyte.MaxValue);
        state.FrontWheelRpm = ToShort(fl.wheelCollider.rpm);
        state.RearWheelRpm = ToShort(rl.wheelCollider.rpm);
        state.Slip = CarState.PackSlip(Mathf.Max(fl.totalSlip_skidMarks, fr.totalSlip_skidMarks), Mathf.Max(rl.totalSlip_skidMarks, rr.totalSlip_skidMarks));
        state.Smoke = (byte)((Smoking(fl) || Smoking(fr) ? CarState.SmokeFront : 0) | (Smoking(rl) || Smoking(rr) ? CarState.SmokeRear : 0));
    }

    private static short ToShort(float rpm) => (short)Mathf.Clamp(rpm, short.MinValue, short.MaxValue);

    private static bool Smoking(RCC_WheelCollider wheel) => wheel.totalSlip > wheel.startSlipValue;

    public void Tick(IEnumerable<Remote> remotes, Vector3 listener)
    {
        foreach (var remote in remotes)
        {
            if (remote.Controller == null || !remote.Car.activeSelf || _builds.IsFitting(remote)
                || (remote.Car.transform.position - listener).sqrMagnitude > RangeMetres * RangeMetres)
            {
                // nothing drives these wheels now, don't leave a cloud pouring out
                foreach (var wheel in remote.Wheels)
                    SetSmoke(remote, wheel, false);
                continue;
            }
            if (remote.WheelsCar != remote.Car)
                Find(remote);
            foreach (var wheel in remote.Wheels)
                Drive(remote, wheel);
        }
    }

    private static void Find(Remote remote)
    {
        remote.WheelsCar = remote.Car;
        remote.Wheels.Clear();
        var car = remote.Controller;
        var custom = remote.Car.GetComponentInChildren<CarLocalCustom>(true);
        Add(remote, car.FrontLeftWheelCollider, custom, true);
        Add(remote, car.FrontRightWheelCollider, custom, true);
        Add(remote, car.RearLeftWheelCollider, custom, false);
        Add(remote, car.RearRightWheelCollider, custom, false);
        var first = remote.Wheels.Count > 0 ? remote.Wheels[0] : null;
        var data = first?.Wheel.carData;
        NetBehaviour.Log.LogInfo($"Wheels for {remote.Name}: {remote.Wheels.Count} found, skidmarks {first?.Marks != null}, camber {data?.camberFront}/{data?.camberRear}");
    }

    private static void Add(Remote remote, RCC_WheelCollider wheel, CarLocalCustom custom, bool front)
    {
        if (wheel == null || wheel.wheelModel == null)
            return;
        var visual = new WheelVisual
        {
            Wheel = wheel,
            Model = wheel.wheelModel,
            Mesh = wheel.wheelMesh != null && wheel.wheelMesh.IsChildOf(wheel.wheelModel) && wheel.wheelMesh != wheel.wheelModel ? wheel.wheelMesh : null,
            Rest = wheel.wheelModel.localRotation,
            Front = front,
            Right = remote.Car.transform.InverseTransformPoint(wheel.transform.position).x > 0f,
            Ground = GroundLayers(wheel.gameObject.layer),
        };
        // a caliper on the spinning part stays put. only needed when there's no wheelMesh and the whole model spins
        if (visual.Mesh == null && wheel.brakeMesh != null && wheel.brakeMesh.parent == wheel.wheelModel)
        {
            visual.Brake = wheel.brakeMesh;
            visual.BrakeRest = wheel.brakeMesh.localRotation;
        }
        if (custom != null)
        {
            visual.Marks = custom.skidmarks;
            if (custom.carData != null)
                visual.MarkWidth = front ? custom.carData.tireWidthSkidMarkFront : custom.carData.tireWidthSkidMarkRear;
        }
        remote.Wheels.Add(visual);
    }

    private static void Drive(Remote remote, WheelVisual wheel)
    {
        var drive = remote.Drive;
        var rpm = wheel.Front ? drive.FrontWheelRpm : drive.RearWheelRpm;
        wheel.Spin = Mathf.Repeat(wheel.Spin + rpm * DegreesPerRpmSecond * Time.deltaTime, 360f);
        if (wheel.Front)
            wheel.Steer = Mathf.MoveTowards(wheel.Steer, drive.Steer, SteerPerSecond * Time.deltaTime);

        // same as WheelAlign: the model gets the wheel collider's own rotation with steer and camber on top, and the spin goes on
        // wheelMesh under it, turned 180 on the right side. camber is the size from CarData, the side picks the sign
        // (right leans one way, left the other). the game adds a bit of camber from body roll too, a ghost has no roll to read
        var data = wheel.Wheel.carData;
        var camber = Mathf.Min(Mathf.Abs(data == null ? 0f : wheel.Front ? data.camberFront : data.camberRear), MaxCamber);
        var tilt = Quaternion.Euler(0f, wheel.Steer, wheel.Right ? camber : -camber);
        if (wheel.Mesh != null)
        {
            wheel.Model.rotation = wheel.Wheel.transform.rotation * tilt;
            wheel.Mesh.localRotation = Quaternion.Euler(wheel.Right ? -wheel.Spin : wheel.Spin, wheel.Right ? 180f : 0f, 0f);
        }
        else
        {
            wheel.Model.localRotation = tilt * Quaternion.Euler(wheel.Spin, 0f, 0f) * wheel.Rest;
            if (wheel.Brake != null)
                wheel.Brake.rotation = wheel.Model.parent.rotation * tilt * wheel.Rest * wheel.BrakeRest;
        }
        Settle(wheel);

        Skid(wheel, wheel.Front ? drive.FrontSlip : drive.RearSlip);
        SetSmoke(remote, wheel, (drive.Smoke & (wheel.Front ? CarState.SmokeFront : CarState.SmokeRear)) != 0);
    }

    // the game puts the wheel where its suspension sits, a ghost's wheel collider is off so it never moves off its mount.
    // same sum the collider does: from the mount down to the road, no further than the suspension travel. WheelAlign puts the
    // model on the mount's own position and only moves it along up, so any sideways part of the collider's center is dropped
    private static void Settle(WheelVisual visual)
    {
        var collider = visual.Wheel.wheelCollider;
        if (collider == null)
            return;
        var mount = collider.transform;
        var up = mount.up;
        var top = mount.position + up * Vector3.Dot(up, mount.TransformPoint(collider.center) - mount.position);
        var radius = collider.radius * mount.lossyScale.y;
        var travel = collider.suspensionDistance * mount.lossyScale.y;
        var drop = travel * (1f - collider.suspensionSpring.targetPosition);
        var count = Physics.RaycastNonAlloc(top, -up, Hits, travel + radius, visual.Ground, QueryTriggerInteraction.Ignore);
        for (var i = 0; i < count; i++)
        {
            var hit = Hits[i];
            if (hit.distance - radius < drop && !hit.transform.IsChildOf(visual.Model.root))
                drop = Mathf.Max(0f, hit.distance - radius);
        }
        visual.Drop = visual.Drop < 0f ? drop : Mathf.MoveTowards(visual.Drop, drop, DropPerSecond * Time.deltaTime);
        visual.Model.position = top - up * visual.Drop;
    }

    // whatever the wheel collider itself would roll on
    private static int GroundLayers(int wheel)
    {
        var mask = 0;
        for (var layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(wheel, layer))
                mask |= 1 << layer;
        return mask & Physics.DefaultRaycastLayers;
    }

    // the game's SkidMarks writes -1 over the wheel's own chain, so the chain lives on my side
    private static void Skid(WheelVisual visual, float slip)
    {
        var marks = visual.Marks;
        if (marks == null || slip < MinSkid
            || !Physics.Raycast(visual.Model.position + Vector3.up * RayLift, Vector3.down, out var hit, RayLength, visual.Ground, QueryTriggerInteraction.Ignore)
            || hit.transform.IsChildOf(visual.Model.root))
        {
            visual.Chain = -1;
            return;
        }
        marks.target_pos = hit.point;
        marks.target_normal = hit.normal;
        marks.target_intensity = slip;
        marks.target_lastIndex = visual.Chain;
        marks.target_markWidth = visual.MarkWidth > 0f ? visual.MarkWidth : DefaultMarkWidth;
        visual.Chain = marks.AddSkidMark();
    }

    // the game makes these in the wheel's own Update, which a ghost never runs. only the first ground, a ghost doesn't know what it's on
    private static void SetSmoke(Remote remote, WheelVisual visual, bool on)
    {
        visual.SmokeAge = on ? visual.SmokeAge + Time.deltaTime : 0f;
        if (!visual.SmokeMade)
        {
            if (!on)
                return;
            visual.SmokeMade = true;
            MakeSmoke(remote, visual);
        }
        if (on)
            Thicken(visual);
        if (on == visual.SmokeOn)
            return;
        visual.SmokeOn = on;
        foreach (var smoke in visual.Smoke)
        {
            if (smoke == null)
                continue;
            var emission = smoke.emission;
            emission.enabled = on;
            if (on && !smoke.isPlaying)
                smoke.Play();
        }
    }

    // same curve as the game's own wheels, the age is mine since a ghost never runs it
    private static void Thicken(WheelVisual visual)
    {
        var settings = RCC_Settings.Instance;
        if (settings == null)
            return;
        var build = Mathf.Clamp01((visual.SmokeAge - SmokeBuildAfter) / SmokeBuildSeconds);
        build *= build;
        var thin = settings.smokeStart;
        thin.a *= SmokeThin;
        var colour = Color.LerpUnclamped(thin, settings.smokeEnd, build);
        var size = settings.smokeStartSize * SmokeSizeScale * (build * SmokeSizeGrowth + SmokeThin);
        var rate = build * SmokeRateGrowth + SmokeRateThin;
        for (var i = 0; i < visual.Smoke.Count; i++)
        {
            var smoke = visual.Smoke[i];
            if (smoke == null)
                continue;
            var main = smoke.main;
            main.startColor = new ParticleSystem.MinMaxGradient(colour);
            main.startSize = new ParticleSystem.MinMaxCurve(size);
            var emission = smoke.emission;
            emission.rateOverTimeMultiplier = visual.SmokeRates[i] * rate;
        }
    }

    // a prefab can hold several systems and they all play on spawn, so every one gets switched off here and handled in SetSmoke.
    // world space leaves the cloud on the road, on the wheel it would ride along with the car
    private static void MakeSmoke(Remote remote, WheelVisual visual)
    {
        var wheel = visual.Wheel;
        var frictions = RCC_GroundMaterials.Instance?.frictions;
        if (frictions == null)
            return;
        foreach (var ground in frictions)
        {
            if (ground.groundParticles == null)
                continue;
            var spawned = UnityEngine.Object.Instantiate(ground.groundParticles, wheel.transform.position, wheel.transform.rotation);
            spawned.transform.SetParent(wheel.transform, true);
            foreach (var smoke in spawned.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = smoke.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetimeMultiplier = Mathf.Min(main.startLifetimeMultiplier, SmokeLifeSeconds);
                var emission = smoke.emission;
                emission.enabled = false;
                visual.Smoke.Add(smoke);
                visual.SmokeRates.Add(emission.rateOverTimeMultiplier);
            }
            NetBehaviour.Log.LogInfo($"Smoke on {remote.Name}'s car: {spawned.name}, {visual.Smoke.Count} systems");
            return;
        }
    }
}
