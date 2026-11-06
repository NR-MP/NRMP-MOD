using System.Collections.Generic;
using UnityEngine;

namespace NightRunnersMP;

// no walk anim in the game so figures swing their own bones. angles around the body's axes not the bones', works on any rig.
// runs after the animator from the standing pose, hands back once stopped
internal sealed class FigureGait
{
    private const float MovingSpeed = 0.3f;
    private const float RunSpeed = 5f;
    private const float BlendSeconds = 0.2f;
    private const float WalkStride = 0.7f;
    private const float RunStride = 1.3f;

    private const float WalkThigh = 24f;
    private const float RunThigh = 42f;
    private const float WalkKnee = 45f;
    private const float RunKnee = 85f;
    private const float WalkArm = 16f;
    private const float RunArm = 38f;
    private const float WalkElbow = 12f;
    private const float RunElbow = 65f;
    private const float FootLevel = 0.6f;
    private const float WalkLean = 2f;
    private const float RunLean = 12f;
    private const float Twist = 6f;
    private const float Bob = 0.025f;

    private static readonly (HumanBodyBones Bone, string Name)[] Wanted =
    {
        (HumanBodyBones.Hips, "Hips"),
        (HumanBodyBones.Spine, "Spine"),
        (HumanBodyBones.LeftUpperLeg, "LeftUpLeg"),
        (HumanBodyBones.LeftLowerLeg, "LeftLeg"),
        (HumanBodyBones.LeftFoot, "LeftFoot"),
        (HumanBodyBones.RightUpperLeg, "RightUpLeg"),
        (HumanBodyBones.RightLowerLeg, "RightLeg"),
        (HumanBodyBones.RightFoot, "RightFoot"),
        (HumanBodyBones.LeftUpperArm, "LeftArm"),
        (HumanBodyBones.LeftLowerArm, "LeftForeArm"),
        (HumanBodyBones.RightUpperArm, "RightArm"),
        (HumanBodyBones.RightLowerArm, "RightForeArm"),
    };

    private const int Hips = 0;
    private const int Spine = 1;
    private const int LeftLeg = 2;
    private const int RightLeg = 5;
    private const int LeftArm = 8;
    private const int RightArm = 10;

    private readonly Transform[] _bones = new Transform[Wanted.Length];
    private readonly Quaternion[] _rest = new Quaternion[Wanted.Length];
    private Vector3 _restHips;
    private float _weight;
    private float _phase;

    private FigureGait()
    {
    }

    public static FigureGait TryCreate(Animator animator)
    {
        if (animator == null)
            return null;
        var gait = new FigureGait();
        if (animator.isHuman)
            for (var i = 0; i < Wanted.Length; i++)
                gait._bones[i] = animator.GetBoneTransform(Wanted[i].Bone);
        else
            gait.FindByName(animator.transform);
        foreach (var bone in gait._bones)
            if (bone == null)
                return null;
        return gait;
    }

    private void FindByName(Transform root)
    {
        var open = new Stack<Transform>();
        open.Push(root);
        while (open.Count > 0)
        {
            var bone = open.Pop();
            var name = bone.name;
            for (var i = 0; i < Wanted.Length; i++)
                if (_bones[i] == null && name.EndsWith(Wanted[i].Name, System.StringComparison.Ordinal))
                    _bones[i] = bone;
            for (var i = 0; i < bone.childCount; i++)
                open.Push(bone.GetChild(i));
        }
    }

    public void Apply(float speed, float deltaTime, Transform body)
    {
        var target = speed > MovingSpeed ? 1f : 0f;
        if (_weight <= 0f)
        {
            if (target <= 0f)
                return;
            Capture();
        }
        _weight = Mathf.MoveTowards(_weight, target, deltaTime / BlendSeconds);

        var pace = Mathf.Clamp01(speed / RunSpeed);
        var stride = Mathf.Lerp(WalkStride, RunStride, pace);
        _phase = Mathf.Repeat(_phase + speed * deltaTime / stride * Mathf.PI, Mathf.PI * 2f);

        Restore();
        var right = body.right;
        var up = body.up;
        var thigh = Mathf.Lerp(WalkThigh, RunThigh, pace) * _weight;
        var knee = Mathf.Lerp(WalkKnee, RunKnee, pace) * _weight;
        var arm = Mathf.Lerp(WalkArm, RunArm, pace) * _weight;
        var elbow = Mathf.Lerp(WalkElbow, RunElbow, pace) * _weight;

        _bones[Hips].position += up * (Bob * Mathf.Cos(_phase * 2f) * _weight);
        var spine = _bones[Spine];
        Turn(spine, right, Mathf.Lerp(WalkLean, RunLean, pace) * _weight);
        Turn(spine, up, Mathf.Sin(_phase) * Twist * _weight);

        Leg(LeftLeg, right, _phase, thigh, knee);
        Leg(RightLeg, right, _phase + Mathf.PI, thigh, knee);
        Arm(LeftArm, right, _phase, arm, elbow);
        Arm(RightArm, right, _phase + Mathf.PI, arm, elbow);
    }

    public void Release()
    {
        if (_weight > 0f)
            Restore();
        _weight = 0f;
    }

    private void Leg(int first, Vector3 right, float phase, float thigh, float knee)
    {
        // around body right, + takes a hanging limb backward
        var swing = -Mathf.Sin(phase) * thigh;
        var bend = Mathf.Max(0f, Mathf.Cos(phase)) * knee;
        Turn(_bones[first], right, swing);
        Turn(_bones[first + 1], right, bend);
        Turn(_bones[first + 2], right, -(swing + bend) * FootLevel);
    }

    private void Arm(int first, Vector3 right, float phase, float arm, float elbow)
    {
        Turn(_bones[first], right, Mathf.Sin(phase) * arm);
        Turn(_bones[first + 1], right, -elbow * (0.6f + 0.4f * Mathf.Cos(phase)));
    }

    private static void Turn(Transform bone, Vector3 axis, float degrees) =>
        bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;

    private void Capture()
    {
        for (var i = 0; i < _bones.Length; i++)
            _rest[i] = _bones[i].localRotation;
        _restHips = _bones[Hips].localPosition;
    }

    private void Restore()
    {
        for (var i = 0; i < _bones.Length; i++)
            _bones[i].localRotation = _rest[i];
        _bones[Hips].localPosition = _restHips;
    }
}
