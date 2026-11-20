using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using PlanetJem.Core.FloatingOrigin;
using PlanetJem.Environment;
using PlanetJem.Roads;
using UnityEngine;
using UnityEngine.AzureSky;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace NightRunnersMP;

internal sealed class Haruna
{
    private const float UnloadTimeout = 15f;

    private readonly ManualLogSource _log;
    private readonly List<(Behaviour Part, bool Enabled)> _parts = new();
    private Scene _scene;
    private RoadNetwork _roads;
    private bool _roadsOn;
    private Sky _sky;
    private UniTask _unload;
    private float _unloadUntil;

    public bool Captured { get; private set; }

    public bool Suspended { get; private set; }

    public bool Streamed { get; private set; }

    public bool Unloading { get; private set; }

    public Haruna(ManualLogSource log) => _log = log;

    public void Capture()
    {
        Captured = true;
        _scene = SceneManager.GetActiveScene();
        _parts.Clear();
        _sky = new Sky();
        foreach (var volume in Resources.FindObjectsOfTypeAll<PostProcessVolume>())
            if (volume != null && volume.isGlobal && volume.gameObject.scene.IsValid())
                _parts.Add((volume, volume.enabled));
        foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
            if (light != null && light.gameObject.scene == _scene && light.GetComponentInParent<RCC_CarControllerV3>() == null)
                _parts.Add((light, light.enabled));
        AddSky<AzureSkyManager>();
        AddSky<AzureSkyEffects>();
        AddSky<AzureSkyFogScattering>();
        AddSky<AzureSkyWeatherZone>();
        AddSky<AzureThunderEffect>();
        AddSky<AzureCameraLookAt>();
        _roads = Resources.FindObjectsOfTypeAll<RoadNetwork>().FirstOrDefault(r => r != null && r.gameObject.scene.IsValid());
        _roadsOn = _roads != null && _roads.enabled;
        Streamed = MapChunkStreamer.Instance != null;
        _log.LogInfo($"Map travel: Haruna captured, scene {_scene.name}, {_parts.Count} volumes, lights and sky parts, roads {(_roads != null ? "found" : "not found")}, streamer {(Streamed ? "found" : "not found")}");
    }

    public void Suspend()
    {
        if (!Captured || Suspended)
            return;
        Suspended = true;
        if (_roads != null)
        {
            _roads.enabled = false;
            var manager = FloatingOriginManager.Instance;
            if (manager != null && manager.shiftHandlers_ != null)
                manager.shiftHandlers_ = manager.shiftHandlers_.Where(h => h != _roads).ToArray();
            manager?.CacheShiftHandlers();
        }
        if (Streamed)
        {
            MapChunkStreamer.StopStreaming();
            _unload = MapChunkStreamer.UnloadAllAsync(Il2CppSystem.Threading.CancellationToken.None);
            _unloadUntil = Time.unscaledTime + UnloadTimeout;
            Unloading = true;
        }
        _log.LogInfo($"Map travel: Haruna road network {(_roads != null ? "off and out of the shift handlers" : "not found")}, chunk streaming {(Streamed ? "stopped, unloading chunks" : "not found")}");
    }

    public void Tick()
    {
        if (!Unloading)
            return;
        var timedOut = Time.unscaledTime >= _unloadUntil;
        if (_unload.Status == UniTaskStatus.Pending && !timedOut)
            return;
        Unloading = false;
        _log.LogInfo(timedOut ? "Map travel: Haruna chunks took too long to unload, going anyway" : "Map travel: Haruna chunks unloaded");
    }

    private void AddSky<T>() where T : Behaviour
    {
        foreach (var part in Resources.FindObjectsOfTypeAll<T>())
            if (part != null && part.gameObject.scene.IsValid())
                _parts.Add((part, part.enabled));
    }

    public void Activate(Scene map)
    {
        var active = map.IsValid() && map.isLoaded && SceneManager.SetActiveScene(map);
        foreach (var (part, _) in _parts)
            if (part != null)
                part.enabled = false;
        DynamicGI.UpdateEnvironment();
        _log.LogInfo($"Map travel: active scene {(active ? map.name : $"not set, still {SceneManager.GetActiveScene().name}")}, Haruna volumes, lights and sky off");
    }

    public void Restore()
    {
        if (!Captured)
            return;
        if (_scene.IsValid() && _scene.isLoaded)
            SceneManager.SetActiveScene(_scene);
        _sky?.Apply();
        foreach (var (part, enabled) in _parts)
            if (part != null)
                part.enabled = enabled;
        if (_roads != null)
        {
            _roads.enabled = _roadsOn;
            var manager = FloatingOriginManager.Instance;
            if (manager != null && manager.shiftHandlers_ != null && !manager.shiftHandlers_.Contains(_roads))
                manager.shiftHandlers_ = manager.shiftHandlers_.Append(_roads).ToArray();
            manager?.CacheShiftHandlers();
        }
        DynamicGI.UpdateEnvironment();
        Suspended = false;
        Unloading = false;
        _log.LogInfo($"Map travel: Haruna environment back, active scene {SceneManager.GetActiveScene().name}, roads {(_roads != null ? "back in the shift handlers" : "not found")}");
    }

    public void Clear()
    {
        Captured = false;
        Suspended = false;
        Streamed = false;
        Unloading = false;
        _parts.Clear();
        _roads = null;
        _sky = null;
    }

    private sealed class Sky
    {
        private readonly Material _skybox = RenderSettings.skybox;
        private readonly AmbientMode _ambientMode = RenderSettings.ambientMode;
        private readonly Color _ambient = RenderSettings.ambientLight;
        private readonly Color _skyColor = RenderSettings.ambientSkyColor;
        private readonly Color _equator = RenderSettings.ambientEquatorColor;
        private readonly Color _groundColor = RenderSettings.ambientGroundColor;
        private readonly float _ambientIntensity = RenderSettings.ambientIntensity;
        private readonly SphericalHarmonicsL2 _probe = RenderSettings.ambientProbe;
        private readonly bool _fog = RenderSettings.fog;
        private readonly FogMode _fogMode = RenderSettings.fogMode;
        private readonly Color _fogColor = RenderSettings.fogColor;
        private readonly float _fogDensity = RenderSettings.fogDensity;
        private readonly float _fogStart = RenderSettings.fogStartDistance;
        private readonly float _fogEnd = RenderSettings.fogEndDistance;
        private readonly DefaultReflectionMode _reflectionMode = RenderSettings.defaultReflectionMode;
        private readonly Texture _reflection = RenderSettings.customReflectionTexture;
        private readonly float _reflectionIntensity = RenderSettings.reflectionIntensity;
        private readonly Light _sun = RenderSettings.sun;

        public void Apply()
        {
            RenderSettings.skybox = _skybox;
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientLight = _ambient;
            RenderSettings.ambientSkyColor = _skyColor;
            RenderSettings.ambientEquatorColor = _equator;
            RenderSettings.ambientGroundColor = _groundColor;
            RenderSettings.ambientIntensity = _ambientIntensity;
            RenderSettings.ambientProbe = _probe;
            RenderSettings.fog = _fog;
            RenderSettings.fogMode = _fogMode;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogDensity = _fogDensity;
            RenderSettings.fogStartDistance = _fogStart;
            RenderSettings.fogEndDistance = _fogEnd;
            RenderSettings.defaultReflectionMode = _reflectionMode;
            RenderSettings.customReflectionTexture = _reflection;
            RenderSettings.reflectionIntensity = _reflectionIntensity;
            RenderSettings.sun = _sun;
        }
    }
}
