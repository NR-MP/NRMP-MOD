using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using Num = System.Numerics;

namespace NightRunnersMP;

// watching a race = my own car rides on the racer's, hidden and frozen. the game's camera, streaming and floating origin
// all follow the player car already, so nothing of the game's has to be told there's a spectator. fast travel gets me there and back
internal sealed class Spectate
{
    private const float SettleSeconds = 4.5f;
    private const float OverSeconds = 6f;
    private const float HideSeconds = 0.5f;
    private const float HomeMetres = 40f;
    private const float ReturnSeconds = 15f;

    private enum Step { Off, Going, Watching, Returning }

    private readonly ManualLogSource _log;
    private readonly Teleports _teleports;
    private readonly Teleports.Locate _locate;
    private readonly Func<Vector3, Num.Vector3> _toWorld;
    private readonly Func<Num.Vector3, Vector3> _toLocal;
    private readonly Dictionary<byte, Remote> _remotes;
    private readonly Action<string> _notice;

    private readonly List<Renderer> _hidden = new();
    private readonly List<Collider> _ghosted = new();
    private Step _step;
    private Remote _target;
    private RCC_CarControllerV3 _car;
    private Rigidbody _body;
    private bool _wasKinematic;
    private Num.Vector3 _home;
    private Quaternion _homeTurn;
    private float _settleAt;
    private float _overAt;
    private float _nextHide;

    public Spectate(ManualLogSource log, Teleports teleports, Teleports.Locate locate, Func<Vector3, Num.Vector3> toWorld,
        Func<Num.Vector3, Vector3> toLocal, Dictionary<byte, Remote> remotes, Action<string> notice)
    {
        _log = log;
        _teleports = teleports;
        _locate = locate;
        _toWorld = toWorld;
        _toLocal = toLocal;
        _remotes = remotes;
        _notice = notice;
    }

    public bool Active => _step != Step.Off;

    public Remote Target => _target;

    public string Banner => _step switch
    {
        Step.Going => $"GOING TO {_target.Tag}'S RACE",
        Step.Watching => $"WATCHING {_target.Tag}{(Rival() is { } rival ? $" VS {rival.Tag}" : "")}  ·  F7 TO STOP",
        Step.Returning => "GOING BACK",
        _ => null,
    };

    // the racer and whoever they race get drawn no matter how far or which scene
    public bool Watches(Remote remote) =>
        _step is Step.Going or Step.Watching && (remote == _target || remote.Id == _target.RaceRival || remote.RaceRival == _target.Id);

    public bool Start(Remote target, RCC_CarControllerV3 car)
    {
        if (Active)
            return false;
        var god = GodConstant.Instance;
        if (car == null || god == null || god.scene_currentType != GodConstant.Scene_currentType.FREEROAM)
        {
            _notice("Drive out onto the road first, then watch the race");
            return false;
        }
        _target = target;
        _home = _toWorld(car.transform.position);
        _homeTurn = car.transform.rotation;
        _settleAt = 0;
        _overAt = 0;
        _step = Step.Going;
        _teleports.Go(_teleports.ToPlayer(target, _locate, false, false));
        _log.LogInfo($"Going to watch {target.Tag}");
        return true;
    }

    public void Stop(string why)
    {
        if (_step is Step.Off or Step.Returning)
            return;
        var watched = _step == Step.Watching;
        _log.LogInfo($"Stopped watching {_target.Tag}: {why}");
        if (why != null)
            _notice(why);
        _target = null;
        if (!watched)
        {
            _step = Step.Off;
            return;
        }
        // still hidden and frozen till it's back, a solid car turning up inside the racer's would shove them
        _step = Step.Returning;
        _settleAt = Time.unscaledTime + ReturnSeconds;
        _teleports.Go(new Teleports.Place
        {
            Name = "where you were",
            Kind = "PLACE",
            Pose = (out Vector3 position, out Quaternion rotation) =>
            {
                position = _toLocal(_home);
                rotation = _homeTurn;
                return true;
            },
        });
    }

    // after the remotes got their pose for this frame
    public void Follow(RCC_CarControllerV3 car)
    {
        if (_step == Step.Off)
            return;
        try
        {
            Run(car);
        }
        catch (Exception e)
        {
            _log.LogError($"Watching failed: {e}");
            Show();
            _target = null;
            _step = Step.Off;
        }
    }

    private void Run(RCC_CarControllerV3 car)
    {
        var now = Time.unscaledTime;
        if (_step == Step.Returning)
        {
            var home = car != null && car == _car && Vector3.Distance(car.transform.position, _toLocal(_home)) < HomeMetres;
            if (!home && now < _settleAt)
                return;
            Show();
            _step = Step.Off;
            return;
        }
        if (!_remotes.TryGetValue(_target.Id, out var still) || still != _target)
        {
            Stop($"{_target.Tag} left");
            return;
        }
        if (_step == Step.Going)
        {
            if (_teleports.Busy || car == null)
                return;
            if (_settleAt == 0)
                _settleAt = now + SettleSeconds;
            if (now < _settleAt)
                return;
            Hide(car);
            _step = Step.Watching;
        }
        if (car == null || car != _car || GodConstant.Instance?.scene_currentType != GodConstant.Scene_currentType.FREEROAM)
        {
            Stop("Stopped watching");
            return;
        }
        if (_target.Racing)
            _overAt = 0;
        else if (_overAt == 0)
            _overAt = now + OverSeconds;
        else if (now >= _overAt)
        {
            Stop("The race is over");
            return;
        }
        if (now >= _nextHide)
            HideMore();
        var on = _target.Car != null && _target.Car.activeSelf ? _target.Car.transform : null;
        if (on != null)
            car.transform.SetPositionAndRotation(on.position, on.rotation);
        else if (_target.Shown && _target.LocalPosition is Vector3 position)
            car.transform.position = position;
    }

    private Remote Rival() => _target != null && _remotes.TryGetValue(_target.RaceRival, out var rival) ? rival : null;

    private void Hide(RCC_CarControllerV3 car)
    {
        _car = car;
        _body = car.GetComponent<Rigidbody>();
        if (_body != null)
        {
            _wasKinematic = _body.isKinematic;
            _body.isKinematic = true;
        }
        foreach (var collider in car.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled)
                continue;
            collider.enabled = false;
            _ghosted.Add(collider);
        }
        HideMore();
    }

    // lights and flames switch their own renderers on while i'm parked on someone
    private void HideMore()
    {
        _nextHide = Time.unscaledTime + HideSeconds;
        foreach (var renderer in _car.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled)
                continue;
            renderer.enabled = false;
            _hidden.Add(renderer);
        }
    }

    private void Show()
    {
        foreach (var renderer in _hidden)
            if (renderer != null)
                renderer.enabled = true;
        foreach (var collider in _ghosted)
            if (collider != null)
                collider.enabled = true;
        _hidden.Clear();
        _ghosted.Clear();
        if (_body != null)
        {
            _body.isKinematic = _wasKinematic;
            if (!_wasKinematic)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }
        _body = null;
        _car = null;
    }
}
