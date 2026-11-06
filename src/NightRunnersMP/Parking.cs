using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using PlanetJem.Races;
using PlanetJem.Walk;
using UnityEngine;

namespace NightRunnersMP;

// game parks by rep so everyone would get the same space. every client sorts spaces the same and hands out by id,
// you keep your space while connected
internal sealed class Parking
{
    private const float NpcClearMeters = 2.5f;
    private const float OwnSpaceDriftMeters = 0.5f;
    private const float MaxSnapMeters = 3f;
    private const float CheckSeconds = 1f;
    private const float WalkerWaitSeconds = 15f;
    private const float StateLogSeconds = 0.5f;
    private const float StateLogRayMeters = 8f;

    private readonly ManualLogSource _log;
    private readonly List<meetSpot_ParkingSpace> _spaces = new();
    private meetSpot _spot;
    private bool _walkerMoved;
    private float _nextCheck;
    private bool _introSeen;
    private float _walkerDeadline;
    private bool _carMoved;
    private Vector3 _parkedFromSpace;
    private Quaternion _fromParked = Quaternion.identity;
    private GameObject _walkerTarget;
    private float _nextStateLog;
    private string _lastState;
    private int _introAngle = -1;
    private bool _gazeLogged;

    public Parking(ManualLogSource log) => _log = log;

    // same two calls as confirming the leave prompt
    public string Leave()
    {
        if (_spot == null)
            return "Not in a PA";
        _spot.meetSpot_leave();
        GodConstant.Instance.floatingPoint_drivingScene.LeaveMeetspot();
        _log.LogInfo("Left the PA");
        return "Leaving";
    }

    // everything the enter car prompt hangs on, logged when it changes
    public void LogEnterCarState(RCC_CarControllerV3 ownCar)
    {
        if (_spot == null || ownCar == null || Time.unscaledTime < _nextStateLog)
            return;
        _nextStateLog = Time.unscaledTime + StateLogSeconds;
        var walker = _spot.localController_;
        var gaze = walker != null ? walker.Gaze : null;
        var eye = Camera.main;
        if (gaze == null || eye == null || !walker.enabled)
            return;
        var active = RCC_SceneManager.Instance != null ? RCC_SceneManager.Instance.activePlayerVehicle : null;
        var look = Physics.Raycast(eye.transform.position, eye.transform.forward, out var hit, StateLogRayMeters, ~0, QueryTriggerInteraction.Ignore)
            ? $"{hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)}"
            : "nothing";
        var state = $"input={walker.CanProcessInput} car={gaze.currentCar_ != null} racer={gaze.currentRacer_ != null} own={gaze.LookingAtPlayerCar}"
            + $" active={active == ownCar} blocked={_spot.player_challengeNoPauseEdgeCase} warnFrame={_spot.meetSpotWarnBlockUntilFrame_ > Time.frameCount}"
            + $" warn={PlanetJem.UI.Hints.UIHints.IsWarnActive} phone={PlanetJem.Phone.PhoneController.IsCallActive} ray={look}"
            + $" game={GameGaze(walker, gaze, eye)} pa={PaGates(_spot)} mycar={CarProbe(walker, ownCar)}";
        if (state != _lastState)
            _log.LogInfo($"Enter car state: {state}");
        _lastState = state;
    }

    // what the game's look ray meets on its way to my own car, and which layers the car's colliders are on. the game only
    // sees colliders on WalkLayers.GazeRay with a CarLocalCustom above them
    private static string CarProbe(LocalController walker, RCC_CarControllerV3 ownCar)
    {
        try
        {
            var look = walker.LookTransform;
            if (look == null)
                return "no look";
            var target = ownCar.transform.position + Vector3.up * 0.5f;
            var toCar = target - look.position;
            var distance = toCar.magnitude;
            var layers = new SortedDictionary<string, int>();
            foreach (var collider in ownCar.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || collider.TryCast<WheelCollider>() != null || !collider.enabled)
                    continue;
                var name = LayerMask.LayerToName(collider.gameObject.layer);
                layers[name] = layers.TryGetValue(name, out var count) ? count + 1 : 1;
            }
            var parts = new List<string> { $"{distance:F0} m" };
            parts.Add(layers.Count == 0 ? "no solid colliders" : string.Join("+", layers.Select(l => $"{l.Value} {l.Key}")));
            if (ownCar.GetComponentInChildren<CarLocalCustom>() == null && ownCar.GetComponentInParent<CarLocalCustom>() == null)
                parts.Add("no CarLocalCustom");
            if (Physics.Raycast(look.position, toCar / distance, out var hit, distance + 3f, (int)WalkLayers.GazeRay, QueryTriggerInteraction.Ignore))
            {
                var mine = hit.collider.transform.IsChildOf(ownCar.transform);
                parts.Add($"ray at it hits {hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)} {(mine ? "mine" : "not mine")} {hit.distance:F0} m");
            }
            else
            {
                parts.Add("ray at it hits nothing");
            }
            return string.Join(", ", parts);
        }
        catch (System.Exception e)
        {
            return $"unreadable {e.GetType().Name}";
        }
    }

    // meetSpot.Update only runs meetSpot_Core (enter car prompt, NPC call) when all of these are clear, read in Ghidra.
    // then meetSpot_Core also wants ui_showWarning off. player calls are ours so they don't need any of it
    private static string PaGates(meetSpot spot)
    {
        try
        {
            var god = GodConstant.Instance;
            var ui = god != null ? god.UI_Data : null;
            var cinematic = spot.introCinematic;
            var text = new System.Text.StringBuilder();
            void Add(bool failing, string name)
            {
                if (failing)
                    text.Append(text.Length == 0 ? name : "," + name);
            }
            Add(spot.meetspotLeave, "leaving");
            Add(god == null, "no god");
            Add(god != null && !god.loading_animationFadeIsFinished, "fade not done");
            Add(god != null && god.gameIsPaused, "paused");
            Add(ui == null, "no ui");
            Add(ui != null && ui.loading_screenIsBlack, "screen black");
            Add(ui != null && ui.ui_showWarning, "warning shown");
            Add(cinematic != null && cinematic.cinematicInProgress, "cinematic");
            Add(PlanetJem.PhotoMode.PhotomodeController.IsActive, "photo mode");
            Add(spot.introAnimInProgress, "intro");
            Add(spot.aiRacerUI_leaveCheck, "leave asked");
            return text.Length == 0 ? "clear" : text.ToString();
        }
        catch (System.Exception e)
        {
            return $"unreadable {e.GetType().Name}";
        }
    }

    // the game's own look check (WalkGaze.Update, read in Ghidra): input on, look pitch not under LookUpBlockThreshold,
    // then one ray from LookTransform along its forward, RayLength long, on WalkLayers.GazeRay, and the hit's parent
    // CarLocalCustom is the car. only the steps that fail get named, so the line stays put while you look around
    private string GameGaze(LocalController walker, WalkGaze gaze, Camera eye)
    {
        try
        {
            var look = walker.LookTransform;
            var config = walker.config_;
            if (look == null || config == null)
                return "no look or config";
            int mask = WalkLayers.GazeRay;
            if (!_gazeLogged)
            {
                _gazeLogged = true;
                var names = new List<string>();
                for (var layer = 0; layer < 32; layer++)
                    if ((mask & (1 << layer)) != 0)
                        names.Add($"{layer} {LayerMask.LayerToName(layer)}");
                _log.LogInfo($"Game gaze: ray {config.RayLength} m, look up block {config.LookUpBlockThreshold}, layers {string.Join(", ", names)}");
            }
            var parts = new List<string>();
            if (!gaze.isActiveAndEnabled)
                parts.Add("gaze off");
            if (gaze.localController_ != walker)
                parts.Add("gaze on another walker");
            if (gaze.lookTransform_ != look)
                parts.Add("gaze on another look");
            var pitch = look.localEulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;
            if (pitch < config.LookUpBlockThreshold)
                parts.Add("looking too far up");
            if (Vector3.Angle(look.forward, eye.transform.forward) > 5f)
                parts.Add("look not where the camera is");
            if (Physics.Raycast(look.position, look.forward, out var hit, config.RayLength, mask, QueryTriggerInteraction.Ignore))
            {
                var car = hit.collider.GetComponentInParent<CarLocalCustom>();
                parts.Add($"{hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)} {(car != null ? "car " + car.name : "no car")}");
            }
            else
            {
                parts.Add("nothing");
            }
            return string.Join(", ", parts);
        }
        catch (System.Exception e)
        {
            return $"unreadable {e.GetType().Name}";
        }
    }

    public bool Inside => _spot != null;

    // Tick stops once i'm offline, so these can still hold spaces the scene already destroyed
    public Transform Anchor => Inside && _spaces.Count > 0 && _spaces[0] != null ? _spaces[0].transform : null;

    public Transform Stop => _spot != null ? _spot.transform : null;

    public meetSpot CurrentSpot => _spot;

    public bool TryGetSpace(byte id, out Transform space)
    {
        space = null;
        if (!Inside || _spaces.Count == 0 || _spaces[id % _spaces.Count] == null)
            return false;
        space = _spaces[id % _spaces.Count].transform;
        return true;
    }

    // space height isn't ground height, so go to the space and let the game's snap set it down.
    // ground not loaded = snap can land miles off, the space is a better guess
    public void Rest(RCC_CarControllerV3 car, Transform space)
    {
        car.transform.SetPositionAndRotation(space.position, space.rotation);
        // snap clears velocity, unity whines about that on a kinematic ghost
        var body = car.GetComponent<Rigidbody>();
        var ghost = body != null && body.isKinematic;
        if (ghost)
            body.isKinematic = false;
        RaceCarPhysics.SnapOntoSurface(car);
        if (ghost)
            body.isKinematic = true;
        if (Mathf.Abs(car.transform.position.y - space.position.y) > MaxSnapMeters)
            car.transform.SetPositionAndRotation(space.position, space.rotation);
    }

    // where i'm standing relative to the first space. false in a car
    public bool TryReadWalker(out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;
        var walker = _spot != null ? _spot.localController_ : null;
        if (walker == null || !walker.isActiveAndEnabled || _spot.introAnimInProgress || Anchor == null)
            return false;
        var body = walker.BodyTransform;
        // body sits up inside the capsule, figures stand from their feet
        var feet = body.position;
        var capsule = walker.characterController_;
        if (capsule != null)
            feet.y = capsule.bounds.min.y;
        position = Anchor.InverseTransformPoint(feet);
        yaw = body.eulerAngles.y - Anchor.eulerAngles.y;
        return true;
    }

    public void Tick(byte ownId, RCC_CarControllerV3 ownCar, IEnumerable<byte> others)
    {
        var god = GodConstant.Instance;
        var spot = god != null && god.scene_currentType == GodConstant.Scene_currentType.MEETSPOT ? god.currentmeetspot : null;
        if (spot == null || spot.parking_playerCar == null)
        {
            _spot = null;
            _spaces.Clear();
            _walkerMoved = false;
            _carMoved = false;
            _introSeen = false;
            return;
        }
        if (_spot == null || _spot.Pointer != spot.Pointer)
        {
            _spot = spot;
            _gazeLogged = false;
            CollectSpaces(spot);
            _nextCheck = 0f;
            _walkerDeadline = Time.unscaledTime + WalkerWaitSeconds;
        }
        if (_spaces.Count == 0)
            return;

        if (Time.unscaledTime >= _nextCheck)
        {
            _nextCheck = Time.unscaledTime + CheckSeconds;
            ParkOwnCar(spot, ownId, ownCar);
            ClearNpcs(spot, ownId);
            foreach (var id in others)
                ClearNpcs(spot, id);
        }
        // game places the walker during the intro, moving it earlier doesn't stick
        _introSeen |= spot.introAnimInProgress;
        LogIntro(spot);
        if (!_walkerMoved && !spot.introAnimInProgress && (_introSeen || Time.unscaledTime >= _walkerDeadline))
            MoveWalker(spot, ownId);
    }

    // sorted, the game shuffles its lists and every client has to get the same order. spaces the game would use first, no truck spots
    private void CollectSpaces(meetSpot spot)
    {
        _spaces.Clear();
        var all = spot.meetSpot_ParkingSpacesOG;
        for (var i = 0; i < all.Count; i++)
        {
            var space = all[i];
            if (space != null && space.carType != meetSpot_ParkingSpace.CarType.only_truck)
                _spaces.Add(space);
        }
        _spaces.Sort((a, b) =>
        {
            var rank = Rank(a).CompareTo(Rank(b));
            if (rank != 0)
                return rank;
            var pa = a.transform.position;
            var pb = b.transform.position;
            return pa.x != pb.x ? pa.x.CompareTo(pb.x) : pa.z.CompareTo(pb.z);
        });
        _log.LogInfo($"{_spaces.Count} player spaces of {all.Count} at {spot.meetSpot_ID}");
    }

    private static int Rank(meetSpot_ParkingSpace space) =>
        space.carType == meetSpot_ParkingSpace.CarType.racer_and_car || space.carType == meetSpot_ParkingSpace.CarType.only_racer ? 0
        : space.carType == meetSpot_ParkingSpace.CarType.racer_and_truck ? 1 : 2;

    private void ParkOwnCar(meetSpot spot, byte ownId, RCC_CarControllerV3 ownCar)
    {
        var space = _spaces[ownId % _spaces.Count];
        var car = spot.parking_playerCar.transform;
        var gap = car.position - space.transform.position;
        gap.y = 0f;
        if (gap.magnitude < OwnSpaceDriftMeters)
            return;

        // relative to the space, world can shift before the walker moves
        _carMoved = true;
        _parkedFromSpace = car.position - space.transform.position;
        _fromParked = space.transform.rotation * Quaternion.Inverse(car.rotation);
        var oldPosition = car.position;
        Rest(ownCar, space.transform);
        // the intro's first panel was put down next to where the game parked you, bring it along
        var panels = spot.intro3DUI_array;
        if (panels != null && panels.Length > 0 && panels[0] != null && !panels[0].transform.IsChildOf(car))
        {
            var panel = panels[0].transform;
            panel.SetPositionAndRotation(car.position + _fromParked * (panel.position - oldPosition), _fromParked * panel.rotation);
        }
        spot.playerParkingSpace_ = space;
        spot.player_parkingSpot = space.transform;
        // the intro's name and money signs sit next to the car, placed once. moved the car after that and they float off screen
        spot.AlignIntroStatsToPlayer();
        _log.LogInfo($"Parked in space {ownId % _spaces.Count} at {car.position}, space height {space.transform.position.y}");
    }

    // one line per intro shot, where its panels are next to the car. for when the PA panels don't show
    private void LogIntro(meetSpot spot)
    {
        var intro = spot.introCinematic;
        var angle = spot.introAnimInProgress && intro != null ? intro.cinematic_angleCount : -1;
        if (angle == _introAngle)
            return;
        _introAngle = angle;
        if (angle < 0)
            return;
        var car = spot.parking_playerCar.transform.position;
        var text = new System.Text.StringBuilder($"PA intro shot {angle}, car at {car}");
        var panels = spot.intro3DUI_array;
        for (var i = 0; panels != null && i < panels.Length; i++)
        {
            var panel = panels[i];
            if (panel != null)
                text.Append($", panel {i} {(panel.activeInHierarchy ? "on" : "off")} {Vector3.Distance(panel.transform.position, car):F1} m from car");
        }
        var camera = Camera.main;
        if (camera != null)
            text.Append($", camera {Vector3.Distance(camera.transform.position, car):F1} m from car");
        _log.LogInfo(text.ToString());
    }

    // game stands you next to your car so keep that in the new space. if it already parked there you're fine
    private void MoveWalker(meetSpot spot, byte ownId)
    {
        _walkerMoved = true;
        var walker = spot.localController_;
        if (walker == null || !_carMoved)
            return;
        var space = _spaces[ownId % _spaces.Count].transform;
        if (_walkerTarget == null)
            _walkerTarget = new GameObject("NRMP walker target");
        // offset from where the game parked the car, put back next to where the car is now. from the space it lost the
        // car's height over the space marker, some spaces sit 0.3 m under the ground and you ended up stood in it
        var offset = walker.transform.position - (space.position + _parkedFromSpace);
        var car = spot.parking_playerCar != null ? spot.parking_playerCar.transform.position : space.position;
        _walkerTarget.transform.SetPositionAndRotation(car + _fromParked * offset, _fromParked * walker.transform.rotation);
        walker.Teleport(_walkerTarget.transform);
        _log.LogInfo($"Walker moved to {_walkerTarget.transform.position}");
    }

    private void ClearNpcs(meetSpot spot, byte id)
    {
        var position = _spaces[id % _spaces.Count].transform.position;
        var racers = spot.parking_racers;
        for (var i = 0; racers != null && i < racers.Count; i++)
            if (racers[i] != null)
                Hide(racers[i].gameObject, position);
        var props = spot.parking_carProps;
        for (var i = 0; props != null && i < props.Count; i++)
            if (props[i] != null)
                Hide(props[i], position);
    }

    private void Hide(GameObject npc, Vector3 position)
    {
        if (!npc.activeSelf)
            return;
        var gap = npc.transform.position - position;
        gap.y = 0f;
        if (gap.magnitude < NpcClearMeters)
        {
            npc.SetActive(false);
            _log.LogInfo($"Cleared {npc.name} from a player space");
        }
    }
}
