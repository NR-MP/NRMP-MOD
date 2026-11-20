using System;
using System.Collections.Generic;
using HarmonyLib;
using NightRunnersMP.Shared;

namespace NightRunnersMP;

internal sealed class RemoteLights
{
    private const float BrakeThreshold = 0.1f;

    private static readonly HashSet<IntPtr> Shown = new();
    private static RCC_CarControllerV3 _lit;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CarLight), "Update"),
            new HarmonyMethod(typeof(RemoteLights), nameof(EngineOn)), new HarmonyMethod(typeof(RemoteLights), nameof(EngineBack)));
    }

    // CarLight skips everything with the engine off, RemoteSound turns it off on far cars
    private static void EngineOn(CarLight __instance)
    {
        _lit = null;
        var car = __instance.rcc;
        if (car == null || car.engineRunning || !Shown.Contains(car.Pointer))
            return;
        _lit = car;
        car.engineRunning = true;
    }

    private static void EngineBack()
    {
        if (_lit != null)
            _lit.engineRunning = false;
        _lit = null;
    }

    public static byte Pack(RCC_CarControllerV3 car) =>
        CarState.PackLights(car.lowBeamHeadLightsOn, car.highBeamHeadLightsOn, car.brakeInput >= BrakeThreshold, (int)car.indicatorsOn);

    public void Tick(IEnumerable<Remote> remotes)
    {
        Shown.Clear();
        foreach (var remote in remotes)
        {
            var car = remote.Controller;
            if (car == null || remote.Car == null || !remote.Car.activeSelf)
                continue;
            var drive = remote.Drive;
            // their engine is on, it's only off here for the sound
            if (drive.Rpm > 0)
                Shown.Add(car.Pointer);
            car.lowBeamHeadLightsOn = drive.LowBeam;
            car.highBeamHeadLightsOn = drive.HighBeam;
            car.brakeInput = drive.Braking ? 1f : 0f;
            car.gasInput = drive.Gas / 255f;
            car.direction = drive.Reverse ? -1 : 1;
            car.indicatorsOn = (RCC_CarControllerV3.IndicatorsOn)drive.Indicators;
            Report(remote, car, drive);
        }
    }

    private static void Report(Remote remote, RCC_CarControllerV3 car, CarState drive)
    {
        var heads = (drive.LowBeam ? 1 : 0) | (drive.HighBeam ? 2 : 0);
        if (remote.LightsCar == car && remote.HeadlightsSeen == heads)
            return;
        remote.LightsCar = car;
        remote.HeadlightsSeen = heads;
        var light = remote.Car.GetComponentInChildren<CarLight>(true);
        NetBehaviour.Log.LogInfo($"Lights for {remote.Name}: low {drive.LowBeam}, high {drive.HighBeam}, car light {(light != null ? light.enabled ? "on" : "off" : "missing")}");
    }
}
