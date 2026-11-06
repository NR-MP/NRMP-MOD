using System.Collections.Generic;
using UnityEngine;

namespace NightRunnersMP;

// other cars' engines. the game's RealisticEngineSound and TurboCharger stay on a ghost, they just need the numbers a driven car has.
// i write those in and the game does the rest, engineRunning off makes it drop its sources again. closest few only
internal sealed class RemoteSound
{
    private const float RangeMetres = 150f;
    private const int MaxEngines = 6;
    private const float PickSeconds = 0.5f;
    private const float RevsPerSecond = 12000f;

    private readonly List<Remote> _near = new();
    private readonly List<Remote> _picked = new();
    private float _nextPick;

    public void Tick(IEnumerable<Remote> remotes, Vector3 listener)
    {
        if (Time.unscaledTime >= _nextPick)
        {
            _nextPick = Time.unscaledTime + PickSeconds;
            Pick(remotes, listener);
        }
        foreach (var remote in _near)
            if (remote.Controller != null)
                Drive(remote);
    }

    private void Pick(IEnumerable<Remote> remotes, Vector3 listener)
    {
        _picked.Clear();
        foreach (var remote in remotes)
            if (remote.Drive.Rpm > 0 && remote.Controller != null && remote.Car.activeSelf
                && (remote.Car.transform.position - listener).sqrMagnitude < RangeMetres * RangeMetres)
                _picked.Add(remote);
        _picked.Sort((a, b) => (a.Car.transform.position - listener).sqrMagnitude.CompareTo((b.Car.transform.position - listener).sqrMagnitude));
        if (_picked.Count > MaxEngines)
            _picked.RemoveRange(MaxEngines, _picked.Count - MaxEngines);

        foreach (var remote in _near)
            if (!_picked.Contains(remote) && remote.Controller != null)
                remote.Controller.engineRunning = false;
        _near.Clear();
        _near.AddRange(_picked);
    }

    private static void Drive(Remote remote)
    {
        var car = remote.Controller;
        if (remote.SoundCar != car)
        {
            remote.SoundCar = car;
            remote.Revs = remote.Drive.Rpm;
            WakeUp(remote);
        }
        var drive = remote.Drive;
        remote.Revs = Mathf.MoveTowards(remote.Revs, drive.Rpm, RevsPerSecond * Time.deltaTime);
        car.engineRunning = true;
        car.fuelInput = 1f;
        car.fuelTank = Mathf.Max(car.fuelTank, 1f);
        car.rawEngineRPM = car.engineRPM = car.engineRPM_UI = car.aiRacer_engineRPM = remote.Revs;
        car.gasInput = drive.Gas / 255f;
        car.currentGear = drive.GearNumber;
        car.direction = drive.Reverse ? -1 : 1;
    }

    // MakeGhost shut every source that existed, the turbo's were among them. the engine makes its own once it runs
    private static void WakeUp(Remote remote)
    {
        var engine = remote.Car.GetComponentInChildren<RealisticEngineSound>(true);
        if (engine != null)
            engine.spatialBlend = 1f;
        var turbo = remote.Car.GetComponentInChildren<TurboCharger>(true);
        if (turbo != null)
        {
            Wake(turbo.whineSource);
            Wake(turbo.twinWhineSource);
            Wake(turbo.airSource);
            Wake(turbo.superSource);
            Wake(turbo.bovSource);
            Wake(turbo.bovTailSource);
        }
        NetBehaviour.Log.LogInfo($"Engine sound for {remote.Name}: engine script {engine != null}, turbo script {turbo != null}");
    }

    private static void Wake(AudioSource source)
    {
        if (source != null)
            source.enabled = true;
    }
}
