using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace NightRunnersMP;

// other players on foot at a stop. no player body in the game so each borrows a figure from the crowd pool
internal sealed class RemoteWalkers
{
    private const float GoneSeconds = 1f;
    private const float RetrySeconds = 5f;
    private const float FollowRate = 12f;
    private const float SpeedRate = 10f;
    private const float MovingSpeed = 0.3f;
    private const float WalkFadeSeconds = 0.15f;
    private const float HeadRoom = 0.5f;
    private const float FigureHeight = 1.7f;
    private const int CrewCount = 14;

    private readonly ManualLogSource _log;
    private bool _clipsLogged;
    private bool _gaitFailed;
    private bool _scaleLogged;

    public RemoteWalkers(ManualLogSource log) => _log = log;

    public void Tick(Parking parking, ushort scene, Dictionary<byte, Remote>.ValueCollection remotes)
    {
        var anchor = parking.Anchor;
        foreach (var remote in remotes)
        {
            var wanted = parking.Inside && anchor != null && remote.Walk.Scene == scene
                && Time.unscaledTime - remote.WalkSeen < GoneSeconds;
            if (!wanted)
            {
                Forget(remote);
                continue;
            }
            var target = new Vector3(remote.Walk.Position.X, remote.Walk.Position.Y, remote.Walk.Position.Z);
            if (remote.Figure == null && !Rent(remote, parking.Stop, target))
                continue;
            Move(remote, anchor, target);
        }
    }

    // after the animators, they write the same bones
    public void Animate(Dictionary<byte, Remote>.ValueCollection remotes)
    {
        if (_gaitFailed)
            return;
        try
        {
            foreach (var remote in remotes)
                if (remote.Figure != null && remote.Gait != null)
                    remote.Gait.Apply(remote.FigureSpeed, Time.unscaledDeltaTime, remote.Figure.transform);
        }
        catch (Exception e)
        {
            _log.LogError($"Walking figures turned off: {e}");
            _gaitFailed = true;
        }
    }

    public void Forget(Remote remote)
    {
        if (remote.Figure != null)
        {
            remote.Gait?.Release();
            remote.Figure.transform.localScale = remote.FigurePoolScale;
            CharacterCrewPoolRegistry.RecycleCharacter(remote.Figure);
        }
        remote.Figure = null;
        remote.Gait = null;
        remote.FigureMoving = false;
        remote.FigureSpeed = 0f;
    }

    public static bool TryGetTag(Remote remote, out Vector3 position)
    {
        position = default;
        if (remote.Figure == null)
            return false;
        var head = remote.Figure.character_headBone;
        position = head != null
            ? head.position + Vector3.up * HeadRoom
            : remote.Figure.transform.position + Vector3.up * (FigureHeight + HeadRoom);
        return true;
    }

    private bool Rent(Remote remote, Transform parent, Vector3 start)
    {
        if (Time.unscaledTime < remote.NextFigure)
            return false;
        remote.NextFigure = Time.unscaledTime + RetrySeconds;
        for (var crew = 0; crew < CrewCount; crew++)
        {
            character_animController figure = null;
            try
            {
                if (!CharacterCrewPoolRegistry.TryGetCharacter((GodConstant.Race_Crews)crew, Vector3.zero, Quaternion.identity,
                        parent, remote.Id, out figure) || figure == null)
                    continue;
            }
            catch (Exception e)
            {
                _log.LogWarning($"No figure from crew {crew}: {e.Message}");
                continue;
            }
            figure.anim_Start(character_animController.CharacterMode.idle, true);
            remote.Figure = figure;
            remote.FigurePoolScale = figure.transform.localScale;
            MatchCrowdSize(figure, parent);
            remote.FigurePosition = start;
            remote.FigureYaw = remote.Walk.Yaw;
            remote.FigureWalkState = FindWalkState(figure);
            if (remote.FigureWalkState == 0)
                remote.Gait = FigureGait.TryCreate(figure.animator_local);
            _log.LogInfo($"{remote.Name} walks as {figure.name} of crew {(GodConstant.Race_Crews)crew}, {(remote.Gait != null ? "with a swung gait" : "no gait")}");
            return true;
        }
        _log.LogWarning($"No figure free for {remote.Name}");
        return false;
    }

    // crowd sits on slots, not the stop, so a figure right under the stop picks up its scale. match the stop's own crowd
    private void MatchCrowdSize(character_animController figure, Transform parent)
    {
        var wanted = figure.transform.localScale;
        try
        {
            foreach (var other in parent.GetComponentsInChildren<character_animController>())
            {
                if (other.transform.parent == parent)
                    continue;
                wanted = other.transform.lossyScale;
                break;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning($"Reading the crowd's size failed: {e.Message}");
        }
        var scale = parent.lossyScale;
        figure.transform.localScale = new Vector3(wanted.x / scale.x, wanted.y / scale.y, wanted.z / scale.z);
        if (_scaleLogged)
            return;
        _scaleLogged = true;
        _log.LogInfo($"Figure size {wanted}, stop scale {scale}");
    }

    // CharacterMode has no walking, look for a walk anim under the animator
    private int FindWalkState(character_animController figure)
    {
        var animator = figure.animator_local;
        var controller = animator != null ? animator.runtimeAnimatorController : null;
        if (controller == null)
            return 0;
        var clips = controller.animationClips;
        var names = new List<string>();
        var found = 0;
        for (var i = 0; i < clips.Length; i++)
        {
            var name = clips[i].name;
            names.Add(name);
            var hash = Animator.StringToHash(name);
            if (found == 0 && name.Contains("walk", StringComparison.OrdinalIgnoreCase) && animator.HasState(0, hash))
                found = hash;
        }
        if (!_clipsLogged)
        {
            _clipsLogged = true;
            _log.LogInfo($"Figure animations: {string.Join(", ", names)}");
        }
        return found;
    }

    private static void Move(Remote remote, Transform anchor, Vector3 target)
    {
        var step = 1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime);
        var before = remote.FigurePosition;
        remote.FigurePosition = Vector3.Lerp(before, target, step);
        var travelled = remote.FigurePosition - before;
        travelled.y = 0f;
        var deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        remote.FigureSpeed = Mathf.Lerp(remote.FigureSpeed, travelled.magnitude / deltaTime,
            1f - Mathf.Exp(-SpeedRate * Time.unscaledDeltaTime));
        var moving = remote.FigureSpeed > MovingSpeed;
        // face where it's going or it slides on a strafe
        var yaw = moving && travelled.sqrMagnitude > 1e-6f ? Mathf.Atan2(travelled.x, travelled.z) * Mathf.Rad2Deg : remote.Walk.Yaw;
        remote.FigureYaw = Mathf.LerpAngle(remote.FigureYaw, yaw, step);
        remote.Figure.transform.SetPositionAndRotation(
            anchor.TransformPoint(remote.FigurePosition),
            anchor.rotation * Quaternion.Euler(0f, remote.FigureYaw, 0f));

        if (moving == remote.FigureMoving || remote.FigureWalkState == 0)
            return;
        remote.FigureMoving = moving;
        if (moving)
            remote.Figure.animator_local.CrossFade(remote.FigureWalkState, WalkFadeSeconds);
        else
            remote.Figure.anim_setState(character_animController.CharacterMode.idle, false);
    }
}
