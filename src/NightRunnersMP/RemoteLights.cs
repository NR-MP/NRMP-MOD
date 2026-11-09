using System.Collections.Generic;
using NightRunnersMP.Shared;

namespace NightRunnersMP;

internal sealed class RemoteLights
{
    private const float BrakeThreshold = 0.1f;

    public static byte Pack(RCC_CarControllerV3 car) =>
        CarState.PackLights(car.lowBeamHeadLightsOn, car.highBeamHeadLightsOn, car.brakeInput >= BrakeThreshold, (int)car.indicatorsOn);

    public void Tick(IEnumerable<Remote> remotes)
    {
        foreach (var remote in remotes)
        {
            var car = remote.Controller;
            if (car == null || remote.Car == null || !remote.Car.activeSelf)
                continue;
            var drive = remote.Drive;
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
