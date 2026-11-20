using System;
using System.Collections.Generic;
using BepInEx.Logging;
using NightRunnersMP.Shared;
using PlanetJem.Input;
using UnityEngine;

namespace NightRunnersMP;

// flash your headlights twice behind someone out on the road, they flash back, countdown, go. first to pull the gap wins, nothing at stake.
// the server isn't in it: each game shows its state as a blob and reads the other's. whoever flashed first calls the go and the winner
internal sealed class Duel
{
    private const float FlashSeconds = 1.2f;
    private const float TargetMetres = 60f;
    private const float TargetDot = 0.8f;
    private const float OfferSeconds = 15f;
    private const float AcceptSeconds = 8f;
    private const float CountdownSeconds = 4f;
    private const float MaxCountdownSeconds = 8f;
    private const float WinMetres = 200f;
    private const float RaceSeconds = 180f;
    private const float ResultSeconds = 6f;

    private readonly ManualLogSource _log;
    private readonly Dictionary<byte, Remote> _remotes;
    private readonly Action<string> _notice;

    private DuelState _state;
    private bool _changed;
    private Remote _rival;
    private Remote _asked;
    private bool _caller;
    private float _lastFlash = float.MinValue;
    private float _until;
    private long _goLocal;
    private float _resultUntil;
    private string _result;
    private bool _failed;

    public Duel(ManualLogSource log, Dictionary<byte, Remote> remotes, Action<string> notice)
    {
        _log = log;
        _remotes = remotes;
        _notice = notice;
    }

    public bool Busy => _state.Phase != DuelPhase.None;

    public string Banner { get; private set; }

    public bool TakeBlob(out byte[] blob)
    {
        blob = _changed ? _state.Write() : null;
        _changed = false;
        return blob != null;
    }

    public void Reset()
    {
        _state = default;
        _rival = _asked = null;
        _changed = false;
        Banner = null;
    }

    // serverDelta: server clock minus mine, in ticks
    public void Tick(RCC_CarControllerV3 car, byte myId, bool free, bool blocked, long serverDelta)
    {
        if (_failed)
            return;
        try
        {
            Banner = Time.unscaledTime < _resultUntil ? _result : null;
            Step(car, myId, free, blocked, serverDelta);
        }
        catch (Exception e)
        {
            _failed = true;
            Banner = null;
            _log.LogError($"Flash races turned off: {e}");
        }
    }

    private void Step(RCC_CarControllerV3 car, byte myId, bool free, bool blocked, long serverDelta)
    {
        var now = Time.unscaledTime;
        if (Busy && (!_remotes.TryGetValue(_rival.Id, out var still) || still != _rival))
        {
            Drop($"{_rival.Tag} left, race off");
            return;
        }
        if (_state.Phase == DuelPhase.Result)
        {
            if (now >= _until)
                Set(DuelPhase.None);
            return;
        }
        if (!free || car == null)
        {
            if (_state.Phase == DuelPhase.Go && _caller && DateTime.UtcNow.Ticks >= _goLocal)
                Finish(_rival.Id, myId);
            else if (Busy)
                Drop("Race off");
            return;
        }

        var flashed = !blocked && Flashed();
        var theirs = _rival?.Duel ?? default;
        var atMe = theirs.Rival == myId;
        switch (_state.Phase)
        {
            case DuelPhase.None:
                Idle(car, myId, flashed, now);
                break;
            case DuelPhase.Offer:
                // both flashed each other at once, the lower slot calls it
                if (atMe && (theirs.Phase == DuelPhase.Accept || theirs.Phase == DuelPhase.Offer && myId < _rival.Id))
                {
                    _caller = true;
                    var goAt = DateTime.UtcNow.Ticks + serverDelta + (long)(CountdownSeconds * TimeSpan.TicksPerSecond);
                    _goLocal = goAt - serverDelta;
                    Set(DuelPhase.Go, goAt);
                }
                else if (atMe && theirs.Phase == DuelPhase.Offer)
                {
                    Set(DuelPhase.Accept);
                    _until = now + AcceptSeconds;
                }
                else if (now >= _until)
                {
                    Drop($"{_rival.Tag} didn't flash back");
                }
                else
                {
                    Banner = $"FLASHED {_rival.Tag}, WAITING FOR THEIR FLASH";
                }
                break;
            case DuelPhase.Accept:
                if (atMe && theirs.Phase == DuelPhase.Go)
                {
                    _caller = false;
                    var ticks = DateTime.UtcNow.Ticks;
                    _goLocal = Math.Clamp(theirs.GoAt - serverDelta, ticks, ticks + (long)(MaxCountdownSeconds * TimeSpan.TicksPerSecond));
                    Set(DuelPhase.Go, theirs.GoAt);
                }
                else if (now >= _until || theirs.Phase == DuelPhase.None)
                {
                    Drop("Race off");
                }
                else
                {
                    Banner = $"RACE {_rival.Tag}, GET READY";
                }
                break;
            case DuelPhase.Go:
                Race(car, myId, theirs, atMe);
                break;
        }
    }

    private void Idle(RCC_CarControllerV3 car, byte myId, bool flashed, float now)
    {
        var offer = OfferAt(myId);
        if (offer != _asked)
        {
            _asked = offer;
            if (offer != null)
                _notice($"{offer.Tag} flashed you, flash your headlights twice to race");
        }
        if (offer != null)
            Banner = $"{offer.Tag} WANTS TO RACE, FLASH TWICE";
        if (!flashed)
            return;
        _rival = offer ?? Ahead(car);
        if (_rival == null)
            return;
        _asked = null;
        if (offer != null)
        {
            Set(DuelPhase.Accept);
            _until = now + AcceptSeconds;
            return;
        }
        Set(DuelPhase.Offer);
        _until = now + OfferSeconds;
        _notice($"Flashed {_rival.Tag}, they flash back to race");
    }

    private void Race(RCC_CarControllerV3 car, byte myId, DuelState theirs, bool atMe)
    {
        var ticks = DateTime.UtcNow.Ticks;
        var went = ticks >= _goLocal;
        if (!_caller && atMe && theirs.Phase == DuelPhase.Result)
        {
            Show(theirs.Winner == myId);
            Set(DuelPhase.None);
            return;
        }
        if (!atMe || theirs.Phase is not (DuelPhase.Go or DuelPhase.Accept or DuelPhase.Result))
        {
            if (_caller && went)
                Finish(myId, myId);
            else
                Drop($"{_rival.Tag} backed out");
            return;
        }
        if (!went)
        {
            var left = Mathf.CeilToInt((_goLocal - ticks) / (float)TimeSpan.TicksPerSecond);
            Banner = left > 3 ? $"RACE {_rival.Tag}" : left.ToString();
            return;
        }
        var seconds = (ticks - _goLocal) / (float)TimeSpan.TicksPerSecond;
        if (!_rival.Shown || _rival.LocalPosition is not Vector3 theirPosition)
        {
            Banner = $"RACE {_rival.Tag}";
            if (_caller && seconds >= RaceSeconds)
                Drop("Race off, lost sight of each other");
            return;
        }
        var apart = car.transform.position - theirPosition;
        var along = car.transform.forward + (_rival.Car != null ? _rival.Car.transform.forward : Vector3.zero);
        if (along.sqrMagnitude < 0.01f)
            along = car.transform.forward;
        var ahead = Vector3.Dot(apart, along.normalized) >= 0f;
        var gap = apart.magnitude;
        Banner = seconds < 1.5f ? "GO" : $"{(ahead ? "AHEAD" : "BEHIND")} {gap:0} m  ·  {WinMetres:0} m wins";
        if (_caller && (gap >= WinMetres || seconds >= RaceSeconds))
            Finish(ahead ? myId : _rival.Id, myId);
    }

    private void Finish(byte winner, byte myId)
    {
        _state.Winner = winner;
        Set(DuelPhase.Result);
        _until = Time.unscaledTime + ResultSeconds;
        Show(winner == myId);
    }

    private void Show(bool won)
    {
        _result = won ? $"YOU BEAT {_rival.Tag}" : $"{_rival.Tag} WINS";
        _resultUntil = Time.unscaledTime + ResultSeconds;
        _notice(won ? $"You beat {_rival.Tag}" : $"{_rival.Tag} won the race");
        _log.LogInfo($"Flash race against {_rival.Tag}: {(won ? "won" : "lost")}");
    }

    private void Drop(string why)
    {
        _notice(why);
        Set(DuelPhase.None);
    }

    private void Set(DuelPhase phase, long goAt = 0)
    {
        _state.Phase = phase;
        _state.Rival = phase == DuelPhase.None ? (byte)0 : _rival.Id;
        _state.GoAt = goAt;
        if (phase != DuelPhase.Result)
            _state.Winner = 0;
        if (phase == DuelPhase.None)
            _rival = null;
        _changed = true;
    }

    private bool Flashed()
    {
        if (!GameInput.Pressed(InputActionId.DriveHeadlights))
            return false;
        var twice = Time.unscaledTime - _lastFlash <= FlashSeconds;
        _lastFlash = twice ? float.MinValue : Time.unscaledTime;
        return twice;
    }

    private Remote OfferAt(byte myId)
    {
        foreach (var remote in _remotes.Values)
            if (remote.Shown && remote.Duel.Phase == DuelPhase.Offer && remote.Duel.Rival == myId)
                return remote;
        return null;
    }

    private Remote Ahead(RCC_CarControllerV3 car)
    {
        Remote best = null;
        var bestDistance = TargetMetres;
        var from = car.transform;
        foreach (var remote in _remotes.Values)
        {
            if (remote.IsBot || !remote.Shown || remote.Car == null || !remote.Car.activeSelf || remote.Duel.Phase != DuelPhase.None)
                continue;
            var to = remote.Car.transform.position - from.position;
            var distance = to.magnitude;
            if (distance >= bestDistance || distance < 0.5f || Vector3.Dot(from.forward, to / distance) < TargetDot)
                continue;
            bestDistance = distance;
            best = remote;
        }
        return best;
    }
}
