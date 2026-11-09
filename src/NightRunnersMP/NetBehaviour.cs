using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Attributes;
using LiteNetLib;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using PlanetJem.Core.FloatingOrigin;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Num = System.Numerics;

namespace NightRunnersMP;

public class NetBehaviour : MonoBehaviour
{
    private const string NoMap = "\n\n";
    private const float ReconnectSeconds = 5f;
    private const int NameLabelRange = 100;
    private const float NameFadeStart = 75f;
    private const float TagSettleSeconds = 2f;
    private float _tagsBackAt;
    // players further than this keep the placeholder (and stay hidden), a spawn only starts once they come closer
    private const float LoadMetres = 300f;
    // a spawned car stays until it's this far, so someone on the edge doesn't flick on and off
    private const float UnloadMetres = 400f;
    private const float RestSeconds = 2f;
    private const float PaintSeconds = 1f;
    private const float NameTagHeight = 2f;
    private const string CrewChatCommand = "/c ";
    private const int WalkSendMs = 100;
    private const int WalkHeartbeatMs = 400;
    private const float WalkMoveMetres = 0.01f;
    private static readonly string[] PlaceholderShaders =
        { "Unlit/Color", "Legacy Shaders/Diffuse", "Sprites/Default", "UI/Default", "Hidden/Internal-Colored" };
    private static readonly Color PlaceholderColor = new(0.8f, 0.25f, 0.1f, 1f);

    public static ManualLogSource Log;
    public static string Host;
    public static int Port;
    public static string JoinKey;
    public static bool SpawnRealCars;
    public static RCC_CarControllerV3 LocalCar;

    private readonly NetDataWriter _writer = new();
    private readonly SendPacer _pacer = new();
    private readonly Health _health = new();
    private readonly Dictionary<byte, Remote> _remotes = new();
    private readonly OriginFrame _origin = new();
    private RemoteCars _cars;
    private Parking _parking;
    private RemoteWalkers _walkers;
    private RemoteBuilds _builds;
    private LiverySync _liveries;
    private RemoteSound _sound;
    private bool _engineOn;
    private RemoteWheels _wheels;
    private readonly RemoteLights _lights = new();
    private readonly TestInput _testInput = new();
    private PlayerCards _cards;
    private readonly List<byte> _here = new();

    private Listener _listener;
    private NetManager _net;
    private NetPeer _peer;
    private bool _welcomed;
    private bool _fatal;
    private byte _myId;
    private string _myName = "";
    private string _steamName = "";
    private byte[] _myPng;
    private Texture2D _myAvatar;
    private bool _myHasPicture;
    private int _sentCar;
    private byte[] _sentPaint;
    private byte[] _sentBuild;
    private byte[] _sentStats;
    private string _sentMap;
    private bool _buildDue = true;
    private float _nextPaint;
    private float _nextConnect;
    private long _nextSendMs;

    // the moment this frame shows, what the local car moves by. clock read mid frame is off by however long the frame took so far
    // and a remote car alongside you shakes at speed
    private double _frameMs;

    private long _nextWalkMs;
    private long _walkSentMs;
    private Vector3 _walkSent;
    private float _walkYawSent;
    private bool _labelErrorLogged;
    private string _status = "offline";
    private Material _placeholderMaterial;

    private RCC_CarControllerV3 _car;
    private CrashGuard _crash;
    private CarHits _hits;
    private Rigidbody _carBody;
    private ushort _scene;
    private string _sceneName;
    private GodConstant.Scene_currentType _lastSceneType;
    private GodConstant.MeetSpots_All _lastStop;

    public static BepInEx.Configuration.ConfigEntry<bool> PlayerCollisions;
    public static GodConstant.MeetSpots_All SpawnStop;
    public static bool SkipIntro;

    public NetBehaviour(IntPtr ptr) : base(ptr) { }

    private Lobby _lobby;
    private GarageTv _tv;
    private MultiplayerMenu _menu;
    private ChatBox _chat;
    private HideUi _hideUi;
    private CarDoors _doors;
    private byte _sentDoors;
    private Traffic _traffic;
    private TrafficSync _shared;
    private byte _sentTrafficFlags;
    private readonly List<Num.Vector3> _watchers = new();
    private PlayerRace _race;
    private RaceCall _call;
    private Teleports _teleports;
    private Maps _maps;
    private CrewState _crews;
    private string _steamId = "";
    // from the welcome, what this pc signs so the server knows it's the same pc as last time
    private byte[] _nonce;
    private CarPicker _picker;
    private EntryWatch _entries;
    private bool _online;

    private void Start()
    {
        _cars = new RemoteCars(Log);
        _parking = new Parking(Log);
        _walkers = new RemoteWalkers(Log);
        _builds = new RemoteBuilds(Log);
        _liveries = new LiverySync(Log, SendRace, _builds);
        _sound = new RemoteSound();
        _wheels = new RemoteWheels(_builds);
        _cards = new PlayerCards(Log);
        _cars.CarReady = StartBuild;
        _builds.Spawning = () => _cars.Busy;
        _lobby = new Lobby(Log, SetOnline, SkipIntro);
        _tv = new GarageTv(Log, ReadSteam, () => _online, SetOnline, AskStatus);
        _picker = new CarPicker(Log, _cars);
        _entries = new EntryWatch(Log);
        // tools/two-players.bat runs two games, the one you tabbed out of has to keep going
        // the game has no setter for this in its dll, so it may not take
        if (Environment.GetEnvironmentVariable("NRMP_TEST") != null)
        {
            try
            {
                Application.runInBackground = true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Run in background failed: {e.Message}");
            }
        }
        _chat = new ChatBox(Log, SendChat);
        _hideUi = new HideUi(Log);
        _doors = new CarDoors(Log);
        _traffic = new Traffic(Log);
        _shared = new TrafficSync(Log, _traffic, (w, method) => _peer?.Send(w, method));
        _race = new PlayerRace(Log, SendRace, () => _parking.CurrentSpot, _remotes, text => _chat.AddNotice(text), () => PlayerStats.OwnTag(_myName));
        _call = new RaceCall(Log, _race, () => PlayerStats.OwnTag(_myName));
        _crews = new CrewState(SendCrewCommand);
        _teleports = new Teleports(Log, _parking.Leave, text => _chat.AddNotice(text));
        _crash = new CrashGuard(Log, text => _chat.AddNotice(text));
        _hits = new CarHits(Log, SendHit);
        _maps = new Maps(Log, _teleports, text => _chat.AddNotice(text)) { Changed = () => { _crash.Reset(); UpdateScene(); } };
        _menu = new MultiplayerMenu(Log, _picker, _crews, _race, _teleports, _maps, LocatePlayer, ApplyCollisions, _traffic);
        // bots have no setting to send, they're always solid
        RemoteCars.TheyAreSolid = car => _remotes.Values.FirstOrDefault(r => r.Car == car) is not { } r || r.IsBot || r.Drive.Solid;
        _listener = new Listener(this);
        _net = new NetManager(_listener) { AutoRecycle = true, DisconnectTimeout = 10000, EnableStatistics = true, UnconnectedMessagesEnabled = true };
        _net.Start();
        // no GUILayout anywhere so OnGUI can skip the layout pass
        useGUILayout = false;
    }

    private void FixedUpdate()
    {
        try
        {
            var free = !InPersonalScene && !_teleports.Busy && !_maps.Busy && _maps.Current == null && GodConstant.Instance?.scene_currentType == GodConstant.Scene_currentType.FREEROAM;
            _crash.FixedTick(_car, free);
            if (!InPersonalScene)
                _hits.FixedTick(_car, _remotes.Values, r => _parking.Inside && _parking.TryGetSpace(r.Id, out _));
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
    }

    private void OnDestroy() => _net?.Stop();

    private void OnApplicationQuit() => _net?.Stop();

    private void Update()
    {
        var start = _health.Start();
        _frameMs += Time.unscaledDeltaTime * 1000.0;
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
        try
        {
            EscGuard.Tick(!(_menu?.IsOpen == true || _chat?.IsTyping == true));
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
        _health.Stop(start);
    }

    // last, so a world shift this frame can't leave remotes a frame behind
    private void LateUpdate()
    {
        _testInput.LateTick();
        var start = _health.Start();
        try
        {
            DrawRemotes(_frameMs, RefreshOrigin());
            _walkers.Animate(_remotes.Values);
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
        try
        {
            DrawTraffic();
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
        _health.Stop(start);
        _health.EndFrame();
        if (_health.Due)
            LogHealth();
    }

    private void LogHealth()
    {
        if (_welcomed)
        {
            var stats = _net.Statistics;
            Log.LogInfo($"Health: mod {_health.AverageMs:F2} ms a frame, worst {_health.WorstMs:F2} ms, "
                + $"{stats.BytesReceived / 1024} KB in, {stats.BytesSent / 1024} KB out, {stats.PacketLoss} lost");
            foreach (var remote in _remotes.Values)
            {
                var i = remote.Interp;
                Log.LogInfo($"Health: {remote.Name} delay {i.TargetDelayMs:F0} ms, ran out {i.RanOut}, pops {i.Pops}, resyncs {i.Resyncs}");
                i.ResetCounts();
            }
            stats.Reset();
        }
        _health.Reset();
    }

    private void Tick()
    {
        _net.PollEvents();
        if (_online && _peer == null && !_fatal && Time.unscaledTime >= _nextConnect)
            Connect();

        var now = (long)_frameMs;
        var offset = RefreshOrigin();
        RefreshPlace();
        if (_welcomed && _car != null && now >= _nextSendMs)
        {
            // stepped from last due time so frame times don't stretch the rate
            _nextSendMs = Math.Max(_nextSendMs + 1000 / Protocol.SendRateHz, now);
            SendState(now, offset);
        }
        if (_welcomed && _car != null && now >= _nextWalkMs)
        {
            _nextWalkMs = now + WalkSendMs;
            SendWalk(now);
        }
        if (_welcomed && _car != null)
            SendModel();
        if (_welcomed && _car != null && Time.unscaledTime >= _nextPaint)
        {
            _nextPaint = Time.unscaledTime + PaintSeconds;
            SendPaint();
            SendStats();
            SendMap();
            ApplyRemotePaints();
        }
        LocalCar = _car;
        if (_welcomed && _car != null && _buildDue)
            SendBuild();
        _walkers.Tick(_parking, _scene, _remotes.Values);
        _testInput.Tick();
        var personal = InPersonalScene;
        _builds.Tick(personal);
        if (!personal)
            _builds.Watch(_remotes.Values, _race.IsRival);
        if (_welcomed)
            _liveries.Tick(_remotes.Values, personal);
        _cards.Tick(_parking, _remotes.Values);
        _lobby.Tick(_car);
        _tv.Tick();
        _teleports.Tick(_car);
        _maps.Tick(_car);
        _call.Tick(_parking, _scene, _myId, _remotes.Values, _menu.IsOpen || _chat.IsTyping);
        _hideUi.Tick(_menu.IsOpen || _chat.IsTyping);
        _chat.Muted = _hideUi.Hidden;
        _doors.Tick(_car, _menu.IsOpen || _chat.IsTyping || personal || _race.Racing, _remotes.Values);
        if (_welcomed && _doors.Open != _sentDoors)
        {
            _sentDoors = _doors.Open;
            SendBlob(BlobKind.Hinges, new[] { (byte)(_sentDoors & CarDoors.All) });
        }
        _traffic.Tick();
        ShareTraffic(now, offset);
    }

    [HideFromIl2Cpp]
    private void ShareTraffic(long now, Num.Vector3 offset)
    {
        if (!_welcomed || _peer == null)
            return;
        var flags = _shared.Flags;
        if (flags != _sentTrafficFlags)
        {
            _sentTrafficFlags = flags;
            _writer.Reset();
            _writer.Put((byte)MsgType.TrafficHello);
            _writer.Put(flags);
            _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }
        if (_car == null)
            return;
        _watchers.Clear();
        foreach (var remote in _remotes.Values)
            if (remote.Interp.Scene == _scene && remote.Interp.TryLast(out var world))
                _watchers.Add(world);
        _shared.TickShare(now, _scene, offset, _watchers, _peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - 2);
    }

    private void DrawTraffic()
    {
        var viewer = _car != null ? _car.transform.position : Vector3.zero;
        _shared.Draw(_frameMs, RefreshOrigin(), viewer, _scene, _car == null || InPersonalScene);
    }

    // garage, car storage and the shops in there are your own little world. everyone else waits till you're back out,
    // new joiners too, then they spawn like normal. no car yet counts, that's the loading in between
    private bool InPersonalScene =>
        !_race.Racing && (_car == null || GodConstant.Instance?.scene_currentType is GodConstant.Scene_currentType.GARAGE or GodConstant.Scene_currentType.STORAGE);

    private void ApplyCollisions()
    {
        foreach (var remote in _remotes.Values)
            if (remote.Car != null)
                RemoteCars.SetCollisions(remote.Car);
    }

    [HideFromIl2Cpp]
    private bool LocatePlayer(Remote remote, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        if (!remote.Interp.TrySample(_frameMs, out var world, out var sampled))
            return false;
        position = ToUnity(_origin.ToLocal(world, RefreshOrigin()));
        rotation = new Quaternion(sampled.X, sampled.Y, sampled.Z, sampled.W);
        return true;
    }

    [HideFromIl2Cpp]
    private void StartBuild(Remote remote)
    {
        PaintRemote(remote);
        if (!remote.WantsBuild)
            return;
        remote.BuildOn = remote.Build;
        _builds.Start(remote);
    }

    [HideFromIl2Cpp]
    private void RemoveRemote(Remote remote)
    {
        RemovePlaceholder(remote);
        _cars.Forget(remote);
        _doors.Forget(remote);
        _walkers.Forget(remote);
        _hits.Forget(remote.Id);
    }

    [HideFromIl2Cpp]
    private static byte[] ReadBuild(CarLocalCustom car)
    {
        try
        {
            var build = CarBuild.Read(car.carLocal_saveID);
            if (build != null && build.Length > Protocol.MaxBlobBytes)
            {
                Log.LogWarning($"Build of {build.Length} bytes is too big to send");
                return null;
            }
            return build;
        }
        catch (Exception e)
        {
            Log.LogWarning($"Reading the car's build failed: {e.Message}");
            return null;
        }
    }

    // comes from the save, so only on a car or place change. leaving a garage counts
    private void SendBuild()
    {
        _buildDue = false;
        var car = _car.carLocal;
        if (car == null)
            return;
        // empty for a car with no save, so nobody keeps fitting the last car's parts
        var build = ReadBuild(car) ?? Array.Empty<byte>();
        if (_sentBuild != null && build.SequenceEqual(_sentBuild))
            return;
        _sentBuild = build;
        Log.LogInfo($"Local car build, {build.Length} bytes");
        SendBlob(BlobKind.Build, build);
    }

    private void SendStats()
    {
        var god = GodConstant.Instance;
        if (god == null || _car.carLocal == null)
            return;
        try
        {
            var stats = PlayerStats.Read(god, _car.carLocal).Write();
            if (_sentStats != null && stats.SequenceEqual(_sentStats))
                return;
            _sentStats = stats;
            SendBlob(BlobKind.Stats, stats);
        }
        catch (Exception e)
        {
            Log.LogWarning($"Reading the player's stats failed: {e.Message}");
        }
    }

    private void SendMap()
    {
        var map = _maps.Current;
        var text = map != null ? $"{map.Id}\n{map.Name}\n{map.Hash}" : NoMap;
        if (text == (_sentMap ?? NoMap))
            return;
        _sentMap = text;
        SendBlob(BlobKind.Map, System.Text.Encoding.UTF8.GetBytes(text));
    }

    [HideFromIl2Cpp]
    private void SendHit(byte target, Vector3 normal, float closing, float mass)
    {
        if (_peer != null)
            SendBlob(BlobKind.Hit, CarHits.Write(target, normal, closing, mass));
    }

    [HideFromIl2Cpp]
    private void SendBlob(BlobKind kind, byte[] blob)
    {
        _writer.Reset();
        _writer.Put((byte)MsgType.SetBlob);
        _writer.Put((byte)kind);
        _writer.Put(blob);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SetOnline(bool online)
    {
        if (_online == online)
            return;
        _online = online;
        _fatal = false;
        _nextConnect = 0f;
        if (!online)
        {
            _net.DisconnectAll();
            _status = "offline";
        }
        Log.LogInfo(online ? "Going online" : "Going offline");
    }

    [HideFromIl2Cpp]
    private void SendRace(System.Action<NetDataWriter> write)
    {
        if (_peer == null)
            return;
        _writer.Reset();
        write(_writer);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    [HideFromIl2Cpp]
    private void SendModel()
    {
        var origin = _car.carLocal != null ? _car.carLocal.carOrigin : null;
        if (origin == null)
            return;
        var signature = CarKey.Signature(origin);
        if (signature == _sentCar)
            return;
        _sentCar = signature;
        var model = CarKey.Of(origin);
        Log.LogInfo($"Local car model {origin.modelType} ({model})");
        _writer.Reset();
        _writer.Put((byte)MsgType.SetCar);
        _writer.Put(model);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    [HideFromIl2Cpp]
    private void SendWalk(long now)
    {
        if (!_parking.TryReadWalker(out var position, out var yaw))
            return;
        // standing still just has to keep showing up
        var still = (position - _walkSent).sqrMagnitude < WalkMoveMetres * WalkMoveMetres && yaw == _walkYawSent;
        if (still && now - _walkSentMs < WalkHeartbeatMs)
            return;
        _walkSentMs = now;
        _walkSent = position;
        _walkYawSent = yaw;
        _writer.Reset();
        _writer.Put((byte)MsgType.Walk);
        new WalkState { Scene = _scene, Position = ToNum(position), Yaw = yaw }.Write(_writer);
        _peer.Send(_writer, DeliveryMethod.Sequenced);
    }

    [HideFromIl2Cpp]
    private void SendPaint()
    {
        if (_car.carLocal == null)
            return;
        var paint = CarPaint.Read(_car.carLocal);
        if (_sentPaint != null && paint.SequenceEqual(_sentPaint))
            return;
        _sentPaint = paint;
        Log.LogInfo($"Local car paint {_car.carLocal.carPaint?.main_colour}");
        _writer.Reset();
        _writer.Put((byte)MsgType.SetPaint);
        _writer.Put(paint);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    // compare against the car itself. new car or the game loading stock colours gets painted again
    [HideFromIl2Cpp]
    private void ApplyRemotePaints()
    {
        foreach (var remote in _remotes.Values)
            PaintRemote(remote);
    }

    [HideFromIl2Cpp]
    private void PaintRemote(Remote remote)
    {
        var god = GodConstant.Instance;
        if (god == null || remote.Paint == null || remote.Car == null)
            return;
        var car = remote.Controller != null ? remote.Controller.carLocal : null;
        if (car == null)
            return;
        var now = CarPaint.Read(car);
        if (remote.PaintedCar == remote.Car && ReferenceEquals(remote.PaintedFrom, remote.Paint) && remote.PaintedRead != null
            && now.SequenceEqual(remote.PaintedRead))
            return;
        var again = remote.PaintedCar == remote.Car && ReferenceEquals(remote.PaintedFrom, remote.Paint);
        try
        {
            CarPaint.Apply(god, car, remote.Paint);
        }
        catch (System.Exception e)
        {
            Log.LogWarning($"Could not paint {remote.Name}'s car: {e.Message}");
            remote.Paint = null;
            return;
        }
        remote.PaintedCar = remote.Car;
        remote.PaintedFrom = remote.Paint;
        remote.PaintedRead = CarPaint.Read(car);
        remote.LiveryCar = null;
        Log.LogInfo($"Painted {remote.Name}'s car {car.carPaint?.main_colour}{(again ? ", the game had changed it" : "")}{(remote.PaintedRead.SequenceEqual(remote.Paint) ? "" : ", reads back different")}");
    }

    private void SendChat(string text)
    {
        if (!_welcomed)
            return;
        if (text.StartsWith("/"))
        {
            SendCommand(text);
            return;
        }
        _writer.Reset();
        _writer.Put((byte)MsgType.Chat);
        _writer.Put(text);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        _chat.Add(PlayerStats.OwnTag(_myName), _myId, _myAvatar, _myHasPicture, text);
    }

    private void SendCommand(string text)
    {
        if (!text.StartsWith(CrewChatCommand))
        {
            _chat.AddNotice($"Unknown command, {CrewChatCommand}message talks to your crew");
            return;
        }
        var said = text.Substring(CrewChatCommand.Length).Trim();
        if (said.Length == 0)
            return;
        _writer.Reset();
        _writer.Put((byte)MsgType.CrewChat);
        _writer.Put(said);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        _chat.AddCrew(_myName, _myAvatar, _myHasPicture, said);
    }

    private void SendCrewCommand(CrewAction action, string text, byte slot)
    {
        if (!_welcomed)
            return;
        _writer.Reset();
        _writer.Put((byte)MsgType.CrewCommand);
        _writer.Put((byte)action);
        _writer.Put(text);
        _writer.Put(slot);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void Identify()
    {
        _writer.Reset();
        _writer.Put((byte)MsgType.Identify);
        _writer.Put(_steamId);
        // an older server has no nonce and reads the steam id only
        if (PcKey.Sign(_steamId, _nonce) is { } signed)
        {
            Wire.PutBytes(_writer, signed.Key);
            Wire.PutBytes(_writer, signed.Signature);
        }
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    // needs steam for the name and pic
    private bool ReadSteam()
    {
        var steam = Steam.Read();
        if (steam == null)
        {
            Log.LogWarning("Steam isn't running, staying offline");
            return false;
        }
        _steamName = steam.Name;
        _steamId = steam.Id;
        var png = Avatars.Read(steam.AvatarFile);
        var picture = Avatars.Decode(png);
        _myPng = picture != null ? png : null;
        _myAvatar = picture;
        _myHasPicture = picture != null;
        Log.LogInfo($"Steam name {steam.Name}, picture {_myHasPicture}");
        return true;
    }

    private void SendPicture()
    {
        if (!_welcomed || _myPng == null)
            return;
        _writer.Reset();
        _writer.Put((byte)MsgType.SetAvatar);
        _writer.Put(_myPng);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void AskStatus()
    {
        if (string.IsNullOrEmpty(Host))
            return;
        var w = new NetDataWriter();
        ServerStatus.WriteAsk(w);
        _net.SendUnconnectedMessage(w, Host, Port);
    }

    private void Connect()
    {
        _nextConnect = Time.unscaledTime + ReconnectSeconds;
        var w = new NetDataWriter();
        new ConnectRequest { ProtocolVersion = Protocol.Version, JoinKey = JoinKey, Name = _steamName }.Write(w);
        _status = $"connecting to {Host}:{Port}";
        _peer = _net.Connect(Host, Port, w);
    }

    [HideFromIl2Cpp]
    private Num.Vector3 RefreshOrigin()
    {
        var manager = FloatingOriginManager.Instance;
        if (manager == null)
        {
            _car = null;
            _carBody = null;
            return default;
        }

        var car = manager.PlayerCar ?? GodConstant.Instance?.playerCar;
        if (car == null)
        {
            _car = null;
            _carBody = null;
            return ToNum(manager.AccumulatedOriginOffset);
        }
        if (_car == null || _car.Pointer != car.Pointer)
        {
            _car = car;
            _carBody = car.GetComponent<Rigidbody>();
            _buildDue = true;
            UpdateScene();
            Log.LogInfo($"Local car found in scene {_sceneName}");
            // only a log, must never block picking up the car
            try
            {
                CarCatalog.LogOnce(Log);
                CarCatalog.LogDealers(Log, _sceneName);
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"Car catalog log failed: {e.Message}");
            }
        }

        return ToNum(manager.AccumulatedOriginOffset);
    }

    // a parking stop sits inside the map scene, so it gets its own place on the wire
    private void RefreshPlace()
    {
        var god = GodConstant.Instance;
        if (god != null && (god.scene_currentType != _lastSceneType || god.player_currentMeetspot != _lastStop))
            UpdateScene();
        if (!_welcomed || _car == null)
            return;
        _here.Clear();
        foreach (var remote in _remotes.Values)
            if (remote.Interp.HasData && remote.Interp.Scene == _scene)
                _here.Add(remote.Id);
        _parking.Tick(_myId, _car, _here);
        _parking.LogEnterCarState(_car);
    }

    private void UpdateScene()
    {
        var god = GodConstant.Instance;
        _lastSceneType = god != null ? god.scene_currentType : default;
        _lastStop = god != null ? god.player_currentMeetspot : default;
        var map = _maps?.Current;
        var scene = map != null ? map.SceneName : _lastSceneType == GodConstant.Scene_currentType.MEETSPOT ? _lastStop.ToString() : SceneManager.GetActiveScene().name;
        if (scene == _sceneName)
            return;
        _sceneName = scene;
        _scene = SceneId.FromName(scene);
        _cars?.ClearFailures();
        _buildDue = true;
        Log.LogInfo($"Now playing in {scene}");
    }

    [HideFromIl2Cpp]
    private void SendState(long now, Num.Vector3 offset)
    {
        var t = _car.transform;
        var rotation = t.rotation;
        // the other side only makes noise once this goes true
        if (_car.engineRunning != _engineOn)
        {
            _engineOn = _car.engineRunning;
            Log.LogInfo($"My engine running {_engineOn}, {_car.engineRPM:0} rpm");
        }
        var state = new CarState
        {
            TimeMs = (uint)PoseTimeMs(now),
            Scene = _scene,
            Position = _origin.ToWorld(ToNum(t.position), offset),
            Rotation = new Num.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
            Velocity = _carBody != null ? ToNum(_carBody.velocity) : default,
            Rpm = _car.engineRunning ? (ushort)Mathf.Clamp(_car.engineRPM, 0f, ushort.MaxValue) : (ushort)0,
            Gas = (byte)(Mathf.Clamp01(_car.gasInput) * 255f),
            Gear = CarState.PackGear(_car.currentGear, _car.direction < 0),
        };
        RemoteWheels.Fill(ref state, _car);
        state.Smoke |= RemoteLights.Pack(_car);
        if (PlayerCollisions.Value)
            state.Smoke |= CarState.SolidBit;
        if (!_pacer.ShouldSend(state, now))
            return;
        _writer.Reset();
        _writer.Put((byte)MsgType.CarState);
        state.Write(_writer);
        state.WriteDrive(_writer);
        _peer.Send(_writer, DeliveryMethod.Sequenced);
    }

    // a car's transform is from earlier than this frame, by a different amount every frame. stamping it with frame time shakes it on other screens
    private long PoseTimeMs(long now)
    {
        if (_carBody == null || _carBody.interpolation == RigidbodyInterpolation.Extrapolate)
            return now;
        var behind = _carBody.interpolation == RigidbodyInterpolation.Interpolate
            ? Time.fixedDeltaTime
            : Time.time - Time.fixedTime;
        return now - (long)(behind * 1000f);
    }

    [HideFromIl2Cpp]
    private void DrawRemotes(double now, Num.Vector3 offset)
    {
        var personal = InPersonalScene;
        foreach (var remote in _remotes.Values)
        {
            var visible = !personal && remote.Interp.HasData && (remote.Interp.Scene == _scene || _race.IsRival(remote)) && (_car != null || _race.IsRival(remote)) && _race.Shows(remote)
                && Near(remote, now, offset);
            remote.Shown = visible;
            if (visible && SpawnRealCars && remote.NeedsSpawn)
                _cars.Request(remote);

            var hasCar = remote.Car != null;
            if (remote.Placeholder == null)
            {
                if (!visible || hasCar)
                    continue;
                remote.Placeholder = CreatePlaceholder(remote.Name);
            }
            SetActive(remote.Placeholder, visible && !hasCar);
            if (hasCar)
                SetActive(remote.Car, visible);
            if (!visible)
                continue;
            var t = (hasCar ? remote.Car : remote.Placeholder).transform;
            if (_parking.Inside && _parking.TryGetSpace(remote.Id, out var space))
            {
                ParkRemote(remote, t, space);
                continue;
            }
            if (!remote.Interp.TrySample(now, out var world, out var rotation))
                continue;
            // true world back to my shifted frame every frame, so no shift handler needed
            remote.LocalPosition = ToUnity(_origin.ToLocal(world, offset));
            t.SetPositionAndRotation(remote.LocalPosition.Value, new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        }
        _cars.Tick(_car, _car != null ? _car.transform.position : default, personal || _builds.Busy, _parking.CurrentSpot);
        _race.Tick();
        _entries.Tick(_race.Racing);
        _picker.Tick();
        if (_car != null && !personal)
        {
            _sound.Tick(_remotes.Values, _car.transform.position);
            _wheels.Tick(_remotes.Values, _car.transform.position);
        }
        if (!personal)
            _lights.Tick(_remotes.Values);
        if (_menu != null)
        {
            _menu.Ping = _peer != null ? _peer.Ping : -1;
        }
    }

    [HideFromIl2Cpp]
    private bool Near(Remote remote, double now, Num.Vector3 offset)
    {
        if (_car == null || _race.IsRival(remote) || _parking.Inside)
            return true;
        if (!remote.Interp.TryLast(out var world))
            return true;
        var range = remote.Car != null && remote.Car.activeSelf ? UnloadMetres : LoadMetres;
        return Vector3.Distance(ToUnity(_origin.ToLocal(world, offset)), _car.transform.position) < range;
    }

    // set down again now and then, ground under a space can stream in after the car
    [HideFromIl2Cpp]
    private void ParkRemote(Remote remote, Transform t, Transform space)
    {
        var controller = remote.Controller;
        if (controller == null)
        {
            remote.LocalPosition = space.position;
            t.SetPositionAndRotation(space.position, space.rotation);
            return;
        }
        if (Time.unscaledTime >= remote.NextRest)
        {
            remote.NextRest = Time.unscaledTime + RestSeconds;
            _parking.Rest(controller, space);
            remote.RestOffset = t.position - space.position;
            if (!remote.RestLogged)
            {
                remote.RestLogged = true;
                Log.LogInfo($"{remote.Name} rests at height {t.position.y}, space height {space.position.y}");
            }
        }
        // relative to the space, world shifts between visits
        remote.LocalPosition = space.position + remote.RestOffset;
        t.SetPositionAndRotation(remote.LocalPosition.Value, space.rotation);
    }

    private static void SetActive(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }
    [HideFromIl2Cpp]
    private GameObject CreatePlaceholder(string name)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = $"NRMP remote {name}";
        var collider = box.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);
        var renderer = box.GetComponent<MeshRenderer>();
        var material = PlaceholderMaterial();
        if (material != null)
            renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        box.transform.localScale = new Vector3(1.9f, 1.3f, 4.5f);
        DontDestroyOnLoad(box);
        return box;
    }

    // primitive's default Standard material goes blown out white noise here. use a simple built-in shader or something the game's drawing
    [HideFromIl2Cpp]
    private Material PlaceholderMaterial()
    {
        if (_placeholderMaterial != null)
            return _placeholderMaterial;
        foreach (var name in PlaceholderShaders)
        {
            var shader = Shader.Find(name);
            if (shader == null || !shader.isSupported)
                continue;
            Log.LogInfo($"Placeholder shader: {name}");
            _placeholderMaterial = new Material(shader) { color = PlaceholderColor };
            return _placeholderMaterial;
        }
        var carRenderer = _car != null ? _car.GetComponentInChildren<MeshRenderer>() : null;
        if (carRenderer != null)
        {
            Log.LogInfo($"Placeholder uses car material {carRenderer.sharedMaterial?.name}");
            _placeholderMaterial = carRenderer.sharedMaterial;
        }
        else
        {
            Log.LogWarning("No placeholder material found");
        }
        return _placeholderMaterial;
    }

    private void OnGUI()
    {
        var start = _health.Start();
        Gui();
        _health.Stop(start);
    }

    private void Gui()
    {
        try
        {
            EscGuard.OnEvent(Event.current);
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape
                && _menu?.IsOpen != true && _chat?.IsTyping != true && !EscGuard.Holding)
                _race?.OnEscape();
            if (Event.current.type == EventType.KeyDown)
                _hideUi?.OnKey(Event.current.keyCode, _menu?.IsOpen == true || _chat?.IsTyping == true);
            if (Event.current.type == EventType.KeyDown)
                _doors?.OnKey(Event.current);
            _chat?.Draw(_welcomed, _menu?.IsOpen == true);
            _menu?.Draw(_welcomed, _chat?.IsTyping == true, _myName, _myAvatar, _myHasPicture, _remotes.Values, _car);

            if (Event.current.type != EventType.Repaint || _hideUi?.Hidden == true)
                return;
            // centred, game's debug text is top left
            if (_online)
            {
                var bar = new GUIContent($"NRMP: {_status} | players {_remotes.Count + (_welcomed ? 1 : 0)} | Unofficial fan mod, not affiliated with PlanetJem");
                var width = GUI.skin.label.CalcSize(bar).x + 8f;
                GUI.Label(new Rect((Screen.width - width) / 2f, 10, width, 24), bar);
            }
            var camera = Camera.main;
            if (camera == null || TagsHidden())
                return;
            foreach (var remote in _remotes.Values)
            {
                if (!remote.Shown)
                    continue;
                var target = remote.Car != null ? remote.Car : remote.Placeholder;
                if (RemoteWalkers.TryGetTag(remote, out var head))
                    DrawTag(camera, head, remote);
                else if (target != null && target.activeSelf)
                    DrawTag(camera, target.transform.position + Vector3.up * NameTagHeight, remote);
            }
        }
        catch (Exception e)
        {
            if (_labelErrorLogged)
                return;
            _labelErrorLogged = true;
            Log.LogError(e);
        }
    }

    // OnGUI draws over loading screens
    private bool TagsHidden()
    {
        if (TagsBlockedNow())
        {
            _tagsBackAt = Time.unscaledTime + TagSettleSeconds;
            return true;
        }
        return Time.unscaledTime < _tagsBackAt;
    }

    private bool TagsBlockedNow()
    {
        if (InPersonalScene)
            return true;
        var god = GodConstant.Instance;
        if (god == null)
            return true;
        if (god.loading_StayInLoadingScreen || god.cinematic_playing || god.gameIsPaused)
            return true;
        var ui = god.UI_Data;
        return ui != null && (ui.loading_fadingToBlack || ui.loading_screenIsBlack);
    }

    [HideFromIl2Cpp]
    private void DrawTag(Camera camera, Vector3 position, Remote remote)
    {
        var p = camera.WorldToScreenPoint(position);
        if (p.z <= 0)
            return;
        var distance = Vector3.Distance(camera.transform.position, position);
        if (_car != null)
            distance = Mathf.Max(distance, Vector3.Distance(_car.transform.position, position) - NameTagHeight);
        if (distance >= NameLabelRange)
            return;
        var fade = Mathf.Clamp01((NameLabelRange - distance) / (NameLabelRange - NameFadeStart));
        var scale = Mathf.Lerp(1.1f, 0.8f, distance / NameLabelRange);
        Avatars.DrawTag(new Vector2(p.x, Screen.height - p.y), remote, fade, scale);
    }

    private void ClearRemotes()
    {
        foreach (var remote in _remotes.Values)
            RemoveRemote(remote);
        _remotes.Clear();
    }

    [HideFromIl2Cpp]
    private static void RemovePlaceholder(Remote remote)
    {
        if (remote.Placeholder != null)
            Destroy(remote.Placeholder);
    }

    [HideFromIl2Cpp]
    private static Num.Vector3 ToNum(Vector3 v) => new(v.x, v.y, v.z);

    [HideFromIl2Cpp]
    private static Vector3 ToUnity(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    // outside the injected class, Il2CppInterop can't expose LiteNetLib's interface there
    private sealed class Listener : INetEventListener
    {
        private readonly NetBehaviour _owner;
        private readonly HashSet<MsgType> _complained = new();

        public Listener(NetBehaviour owner) => _owner = owner;

        public void OnPeerConnected(NetPeer peer) => _owner._status = "connected, waiting for welcome";

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (info.Reason == DisconnectReason.ConnectionRejected && info.AdditionalData.AvailableBytes > 0)
            {
                var reason = (RejectReason)info.AdditionalData.GetByte();
                _owner._status = $"rejected: {reason}";
                _owner._fatal = true;
                Log.LogError($"Server rejected us: {reason}");
            }
            else
            {
                if (_owner._online)
                    _owner._status = $"disconnected: {info.Reason}";
                Log.LogWarning($"Disconnected: {info.Reason}");
            }
            _owner._peer = null;
            _owner._welcomed = false;
            _owner._race.OnLostServer();
            _owner._liveries.Reset();
            _owner._sentCar = 0;
            _owner._sentPaint = null;
            _owner._sentBuild = null;
            _owner._sentStats = null;
            _owner._sentMap = null;
            _owner._sentDoors = 0;
            _owner._sentTrafficFlags = 0;
            _owner._shared.Reset();
            _owner._buildDue = true;
            _owner.ClearRemotes();
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (reader.AvailableBytes < 1)
                return;
            var type = (MsgType)reader.GetByte();
            try
            {
                Receive(type, reader);
            }
            catch (Exception e)
            {
                // a message that doesn't read right gets skipped, it never stops the ones after it
                if (_complained.Add(type))
                    Log.LogWarning($"Skipped a broken {type} message: {e.GetType().Name} {e.Message}");
            }
        }

        private void Receive(MsgType type, NetPacketReader reader)
        {
            switch (type)
            {
                case MsgType.Welcome:
                    _owner._myId = reader.GetByte();
                    _owner._myName = Clean(reader.GetString(), _owner._myId);
                    for (var n = reader.GetByte(); n > 0; n--)
                        Add(reader.GetByte(), reader.GetString()).Model = reader.GetInt();
                    _owner._nonce = null;
                    if (reader.AvailableBytes >= Identity.NonceBytes)
                    {
                        _owner._nonce = new byte[Identity.NonceBytes];
                        reader.GetBytes(_owner._nonce, Identity.NonceBytes);
                    }
                    _owner._welcomed = true;
                    _owner._sentTrafficFlags = 0;
                    _owner._shared.SetMe(_owner._myId);
                    _owner._status = $"online as {_owner._myName}";
                    _owner._myAvatar ??= Avatars.Initial(_owner._myName);
                    _owner.SendPicture();
                    _owner.Identify();
                    Avatars.FindFont();
                    Log.LogInfo($"Joined as player {_owner._myId}, {_owner._remotes.Count} others online");
                    break;
                case MsgType.PlayerJoined:
                    _owner._chat.AddNotice($"{Add(reader.GetByte(), reader.GetString()).Name} joined the server");
                    break;
                case MsgType.PlayerLeft:
                    var id = reader.GetByte();
                    if (_owner._remotes.Remove(id, out var gone))
                    {
                        _owner.RemoveRemote(gone);
                        _owner._chat.AddNotice($"{gone.Name} left the server");
                        Log.LogInfo($"{gone.Name} left");
                    }
                    break;
                case MsgType.CarChanged:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var changed))
                        changed.Model = reader.GetInt();
                    break;
                case MsgType.CarPainted:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var painted))
                    {
                        var paint = reader.GetRemainingBytes();
                        if (CarPaint.Valid(paint))
                            painted.Paint = paint;
                        else
                            Log.LogWarning($"Bad paint from {painted.Name}, {paint.Length} bytes");
                    }
                    break;
                case MsgType.PlayerBlob:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var owner))
                    {
                        var kind = (BlobKind)reader.GetByte();
                        if (kind == BlobKind.Hit)
                            _owner._hits.Receive(owner, _owner._myId, reader.GetRemainingBytes());
                        else
                            ReadBlob(owner, kind, reader.GetRemainingBytes());
                    }
                    break;
                case MsgType.PlayerAvatar:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var pictured))
                    {
                        var picture = Avatars.Decode(reader.GetRemainingBytes());
                        if (picture != null)
                        {
                            pictured.Avatar = picture;
                            pictured.HasPicture = true;
                        }
                    }
                    break;
                case MsgType.ChatMessage:
                    var spoke = reader.GetByte();
                    if (_owner._remotes.TryGetValue(spoke, out var speaker))
                        _owner._chat.Add(speaker.Tag, spoke, speaker.Avatar, speaker.HasPicture, SafeText.Clean(reader.GetString(Protocol.MaxChatLength), Protocol.MaxChatLength, true));
                    break;
                case MsgType.CrewChatMessage:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var mate))
                        _owner._chat.AddCrew(mate.Name, mate.Avatar, mate.HasPicture, SafeText.Clean(reader.GetString(Protocol.MaxChatLength), Protocol.MaxChatLength, true));
                    break;
                case MsgType.CrewList:
                    _owner._crews.Read(reader, _owner._steamId);
                    // the crew tag over a car is what the server says, not what the car's game says
                    foreach (var remote in _owner._remotes.Values)
                        remote.ServerCrew = _owner._crews.CrewOf(remote.Id);
                    break;
                case MsgType.CrewNotice:
                    _owner._chat.AddNotice(reader.GetString());
                    break;
                case MsgType.RaceOffer:
                    _owner._race.OnOffer(reader, _owner._myId);
                    break;
                case MsgType.RaceStart:
                    _owner._race.OnStart(reader);
                    break;
                case MsgType.RaceCancelled:
                    _owner._race.OnCancelled(reader.GetString());
                    break;
                case MsgType.RaceGo:
                    // the server's moment in its own clock, litenetlib's ping keeps the gap between the clocks
                    _owner._race.OnGo(reader.AvailableBytes >= sizeof(long) && _owner._peer != null
                        ? reader.GetLong() - _owner._peer.RemoteTimeDelta : 0);
                    break;
                case MsgType.RaceResult:
                    _owner._race.OnResult(reader.GetByte(), reader.GetInt(), reader.GetInt());
                    break;
                case MsgType.RaceRestage:
                    _owner._race.OnRestage(reader.GetByte());
                    break;
                case MsgType.RaceEnded:
                    _owner._race.OnEnded(reader.GetByte(), (RaceEnd)reader.GetByte());
                    break;
                case MsgType.PlayerWalk:
                    if (_owner._remotes.TryGetValue(reader.GetByte(), out var walker))
                    {
                        var walk = WalkState.Read(reader);
                        if (walk.IsSane)
                        {
                            walker.Walk = walk;
                            walker.WalkSeen = Time.unscaledTime;
                        }
                    }
                    break;
                case MsgType.CarStateBatch:
                    var now = (long)_owner._frameMs;
                    while (reader.AvailableBytes >= 1 + CarState.Size + CarState.DriveSize)
                    {
                        var from = reader.GetByte();
                        var state = CarState.Read(reader);
                        state.ReadDrive(reader);
                        if (state.IsSane && _owner._remotes.TryGetValue(from, out var remote))
                        {
                            remote.Interp.Push(state, now);
                            var flipped = remote.Drive.Solid != state.Solid;
                            remote.Drive = state;
                            if (flipped && remote.Car != null)
                                RemoteCars.SetCollisions(remote.Car);
                        }
                    }
                    break;
                case MsgType.LiveryPart:
                    _owner._liveries.OnPart(reader);
                    break;
                case MsgType.TrafficRole:
                    _owner._shared.OnRole(reader.GetByte(), reader.GetBool());
                    break;
                case MsgType.TrafficSpawn:
                    _owner._shared.OnSpawns(reader);
                    break;
                case MsgType.TrafficBatch:
                    _owner._shared.OnBatch(reader, (long)_owner._frameMs);
                    break;
                case MsgType.TrafficGone:
                    _owner._shared.OnGone(reader);
                    break;
            }
        }

        // anything else is from a newer client
        private static void ReadBlob(Remote remote, BlobKind kind, byte[] blob)
        {
            if (kind == BlobKind.Build)
                ReadBuild(remote, blob.Length > 0 ? blob : null);
            else if (kind == BlobKind.Stats)
                remote.Stats = PlayerStats.Parse(blob);
            else if (kind == BlobKind.Map)
                remote.ReadMap(blob);
            else if (kind == BlobKind.Hinges)
                remote.Hinges = blob.Length > 0 ? (byte)(blob[0] & CarDoors.All) : (byte)0;
        }

        // the same car sent again changes nothing, it only gets rebuilt when the owner actually changed it
        private static void ReadBuild(Remote remote, byte[] build)
        {
            if (build != null && remote.Build != null && build.SequenceEqual(remote.Build))
                return;
            remote.Build = build;
            remote.Rebuilds = 0;
        }

        private Remote Add(byte id, string name)
        {
            if (_owner._remotes.Remove(id, out var bot))
            {
                _owner.RemoveRemote(bot);
            }
            name = Clean(name, id);
            var remote = new Remote { Id = id, Name = name, Avatar = Avatars.Initial(name), ServerCrew = _owner._crews.CrewOf(id) };
            _owner._remotes[id] = remote;
            Log.LogInfo($"{name} joined");
            return remote;
        }

        // the server cleans names too, this is for one that doesn't
        private static string Clean(string name, byte id)
        {
            var clean = SafeText.Clean(name, Protocol.MaxNameLength);
            return clean.Length > 0 ? clean : $"Player {id}";
        }

        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) =>
            Log.LogWarning($"Network error: {socketError}");

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            if (remoteEndPoint.Port == Port && ServerStatus.TryReadReply(reader.RawData, reader.UserDataOffset, reader.UserDataSize, out var online, out var max))
                _owner._tv.Heard(online, max);
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnConnectionRequestRejected() { }
    }
}
