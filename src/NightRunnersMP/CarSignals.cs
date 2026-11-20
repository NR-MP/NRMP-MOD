using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NightRunnersMP.Shared;
using PlanetJem.Races;
using PlanetJem.Roads.Traffic;
using Rewired;
using UnityEngine;

namespace NightRunnersMP;

// horn and NOS. the player has no horn in the game, this borrows the clip racers and traffic honk with.
// NOS on someone else's car goes through the replay call, the flames a replay shows
internal sealed class CarSignals
{
    public static BepInEx.Configuration.ConfigEntry<KeyCode> HornKey;

    private const string HornName = "NRMP horn";
    private const float HornVolume = 0.9f;
    private const float HornNear = 5f;
    private const float HornFar = 160f;
    private const float ClipSearchSeconds = 5f;
    private const float ExhaustSeconds = 2f;

    private readonly ManualLogSource _log;
    private AudioClip _clip;
    private float _nextSearch;
    private AudioSource _myHorn;
    private GameObject _myHornCar;
    private RCC_CarControllerV3 _exhaustCar;
    private Il2CppArrayBase<RCC_Exhaust> _exhausts;
    private float _nextExhausts;
    private bool _nosFailed;
    private bool _hornFailed;

    public CarSignals(ManualLogSource log) => _log = log;

    public byte TickLocal(RCC_CarControllerV3 car, bool blocked)
    {
        byte flags = 0;
        var horn = car != null && !blocked && ReInput.isReady && ReInput.controllers.Keyboard.GetKey(HornKey.Value);
        if (horn)
            flags |= Signal.Horn;
        Honk(ref _myHorn, ref _myHornCar, car != null ? car.gameObject : null, horn);
        if (car != null && ReadNos(car))
            flags |= Signal.Nos;
        return flags;
    }

    public void TickRemotes(IEnumerable<Remote> remotes)
    {
        foreach (var remote in remotes)
        {
            var shown = remote.Shown && remote.Car != null && remote.Car.activeSelf;
            Honk(ref remote.Horn, ref remote.HornCar, remote.Car, shown && (remote.Signals & Signal.Horn) != 0);
            ShowNos(remote, shown && (remote.Signals & Signal.Nos) != 0);
        }
    }

    public void Forget(Remote remote)
    {
        if (remote.Horn != null)
            UnityEngine.Object.Destroy(remote.Horn.gameObject);
        remote.Horn = null;
        remote.HornCar = null;
    }

    private void Honk(ref AudioSource horn, ref GameObject hornCar, GameObject car, bool on)
    {
        if (_hornFailed)
            return;
        try
        {
            if (hornCar != car)
            {
                if (horn != null)
                    UnityEngine.Object.Destroy(horn.gameObject);
                horn = null;
                hornCar = car;
            }
            if (!on || car == null)
            {
                if (horn != null && horn.isPlaying)
                    horn.Stop();
                return;
            }
            if (horn == null)
            {
                var clip = Clip();
                if (clip == null)
                    return;
                horn = Create(car, clip);
            }
            if (!horn.isPlaying)
                horn.Play();
        }
        catch (Exception e)
        {
            _hornFailed = true;
            _log.LogError($"Horn turned off: {e}");
        }
    }

    private static AudioSource Create(GameObject car, AudioClip clip)
    {
        var holder = new GameObject(HornName);
        holder.transform.SetParent(car.transform, false);
        var source = holder.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = HornNear;
        source.maxDistance = HornFar;
        source.volume = HornVolume;
        // the game's own mixer group, so its volume sliders reach the horn too
        var like = car.GetComponentInChildren<AudioSource>(true);
        if (like != null && like != source)
            source.outputAudioMixerGroup = like.outputAudioMixerGroup;
        return source;
    }

    // only looked for while someone wants to honk, the clips aren't loaded before the first road scene
    private AudioClip Clip()
    {
        if (_clip != null || Time.unscaledTime < _nextSearch)
            return _clip;
        _nextSearch = Time.unscaledTime + ClipSearchSeconds;
        foreach (var config in Resources.FindObjectsOfTypeAll<RaceAiConfig>())
        {
            var clips = config.Horn?.Clips;
            for (var i = 0; clips != null && _clip == null && i < clips.Length; i++)
                _clip = clips[i];
            if (_clip != null)
                break;
        }
        if (_clip == null)
        {
            foreach (var traffic in Resources.FindObjectsOfTypeAll<TrafficVehicleVisual>())
            {
                _clip = traffic.hornClip_;
                if (_clip != null)
                    break;
            }
        }
        _log.LogInfo(_clip != null ? $"Horn clip {_clip.name}, {_clip.length:0.0} s" : "No horn clip loaded yet");
        return _clip;
    }

    private bool ReadNos(RCC_CarControllerV3 car)
    {
        if (_nosFailed)
            return false;
        try
        {
            if (car != _exhaustCar || Time.unscaledTime >= _nextExhausts)
            {
                _exhaustCar = car;
                _nextExhausts = Time.unscaledTime + ExhaustSeconds;
                _exhausts = car.GetComponentsInChildren<RCC_Exhaust>(false);
            }
            foreach (var exhaust in _exhausts)
                if (exhaust != null && (exhaust.nosWasPlaying || exhaust.NOS_flame != null && exhaust.NOS_flame.isPlaying))
                    return true;
        }
        catch (Exception e)
        {
            _nosFailed = true;
            _log.LogError($"NOS sync turned off: {e}");
        }
        return false;
    }

    // every frame while it burns, the car's own exhaust script would put the flames out again
    private void ShowNos(Remote remote, bool on)
    {
        if (_nosFailed || !on && !remote.NosOn)
            return;
        remote.NosOn = on;
        try
        {
            var car = remote.Controller != null ? remote.Controller.carLocal : null;
            if (car != null)
                car.ReplaySyncNos(on);
        }
        catch (Exception e)
        {
            _nosFailed = true;
            _log.LogError($"NOS sync turned off: {e}");
        }
    }
}
