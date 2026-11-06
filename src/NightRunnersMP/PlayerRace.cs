using System;
using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;
using PlanetJem.Races;
using UnityEngine;

namespace NightRunnersMP;

// player race = the game's race with the AI swapped out. a racer lends their character under the rival's name,
// so wager, rep, cutscenes, results all run like singleplayer. their car stays hidden and follows the rival (game needs it for positions),
// in the scenes it's the other way round, the rival's car stands where the npc would. hidden car keeps its colliders till the green or it falls
// through the road and takes the rival with it. server releases both starts, times both runs and
// picks the winner, what the game measured here only shows while driving. dev bot = AI just drives, so i can test alone.
// server also calls jump starts and quits, then the game's own Fail settles it: money it took at the start goes back or stays gone
internal sealed class PlayerRace
{
    public const int WagerStep = 1000;

    private const float FindHostSeconds = 0.1f;
    private const float ForfeitSeconds = 3f;
    private const float LoadTimeoutSeconds = 90f;
    private const float ResultsSeconds = 20f;
    private const float QuitAnswerSeconds = 3f;
    private const float RouteSearchMeters = 12f;
    private const float MaxRouteSearchMeters = 60f;
    private const float PositionLogSeconds = 10f;

    public static readonly (int Id, string Name)[] Races =
    {
        ((int)RaceId.MH_SprintFullUphill, "Full mountain uphill"),
        ((int)RaceId.MH_SprintFullDownhill, "Full mountain downhill"),
        ((int)RaceId.MH_SprintTakaneUphill, "Takane uphill"),
        ((int)RaceId.MH_SprintTakaneDownhill, "Takane downhill"),
    };

    private enum Stage
    {
        None,
        Sent,
        Offered,
        Loading,
        Staged,
        Racing,
    }

    private readonly ManualLogSource _log;
    private readonly Action<Action<NetDataWriter>> _send;
    private readonly Func<meetSpot> _spot;
    private readonly Dictionary<byte, Remote> _remotes;
    private readonly Action<string> _notice;
    private readonly Func<string> _myTag;

    // right before the game takes over
    public Action Starting;

    private Stage _stage;
    private Remote _rival;
    private int _raceId;
    private int _wager;
    private bool _second;
    private bool _go;
    // utc ticks on my clock the lights go, 0 = no go yet
    private long _goAt;
    private long _goGot;
    private bool _readySent;
    private bool _named;
    private bool _forfeited;
    private bool _coroutinesStopped;
    private bool _finishSent;
    private bool _opponentFinished;
    private bool _trackingFailed;
    private bool _went;
    private bool _restaging;
    private bool _holdingPause;
    private bool _ending;
    private bool _outro;
    private bool _resultLogged;
    private bool _paidLogged;
    private bool _endContested;
    private bool _endWon;
    private string _endWhy;
    private int? _heldRep;
    private float _quitBy;
    private int _winner = -1;
    private int _winnerMs = -1;
    private int _loserMs = -1;
    private byte _myId;
    private int _counters;
    private int _moneyAtStart;
    private int _loanAtStart;

    private RaceHost _host;
    private readonly List<Renderer> _drawn = new();
    private GameObject _drawnCar;
    private GameObject _ghosted;
    private Vector3? _rivalWas;
    private float _nextPositionLog;
    private RacePhase? _loggedPhase;
    private float _nextDrawn;
    private float _nextFind;
    private float _deadline;
    private float _resultsEnd;
    private float _forfeitUntil;
    private float _nextPaint;
    private pauseMenu _menu;
    private character_carPrefab _character;
    private string _playerName;
    private readonly List<(character_carPrefab Character, string Name)> _renamed = new();

    public PlayerRace(ManualLogSource log, Action<Action<NetDataWriter>> send, Func<meetSpot> spot, Dictionary<byte, Remote> remotes, Action<string> notice, Func<string> myTag)
    {
        _log = log;
        _send = send;
        _spot = spot;
        _remotes = remotes;
        _notice = notice;
        _myTag = myTag;
    }

    public bool Busy => _stage != Stage.None;
    public bool Racing => _stage >= Stage.Loading;
    public bool HasOffer => _stage == Stage.Offered;
    public bool CanCounter => _counters < Protocol.MaxRaceCounters;
    public string RivalName => _rival?.Tag ?? "";
    public byte RivalId => _rival?.Id ?? 0;
    public string RaceName => Array.Find(Races, r => r.Id == _raceId).Name ?? "";
    public int Wager => _wager;
    public int Money => GodConstant.Instance != null ? GodConstant.Instance.player_Money : 0;

    public Remote Find(byte id) => _remotes.TryGetValue(id, out var remote) ? remote : null;

    public bool CanChallenge(Remote remote, ushort scene) =>
        _stage == Stage.None && _spot() != null && (remote.IsBot || remote.Interp.Scene == scene);

    // everyone else stays out of the race, like traffic
    public bool Shows(Remote remote) => !Racing || remote == _rival && !remote.IsBot;

    public bool IsRival(Remote remote) => Racing && remote == _rival;

    public void Challenge(Remote rival, int raceId, int wager, byte myId)
    {
        if (_stage != Stage.None)
            return;
        _myId = myId;
        _rival = rival;
        _raceId = raceId;
        _wager = wager;
        _second = false;
        if (rival.IsBot)
        {
            Begin();
            return;
        }
        _stage = Stage.Sent;
        _send(w =>
        {
            w.Put((byte)MsgType.RaceChallenge);
            w.Put(rival.Id);
            w.Put(raceId);
            w.Put(wager);
        });
        _notice($"Challenge sent to {rival.Tag}");
    }

    public void OnOffer(NetDataReader reader, byte myId)
    {
        var from = reader.GetByte();
        var raceId = reader.GetInt();
        var wager = reader.GetInt();
        if (_stage == Stage.Sent && _rival?.Id == from)
        {
            _wager = wager;
            _counters++;
            _stage = Stage.Offered;
            return;
        }
        if (_stage != Stage.None || !_remotes.TryGetValue(from, out var rival))
        {
            Answer(false);
            return;
        }
        _myId = myId;
        _rival = rival;
        _raceId = raceId;
        _wager = wager;
        _stage = Stage.Offered;
        _notice($"{rival.Tag} challenges you: {RaceName}, {wager:N0}");
    }

    public void Answer(bool accept)
    {
        _send(w =>
        {
            w.Put((byte)MsgType.RaceAnswer);
            w.Put(accept);
        });
        if (!accept && !Racing)
            Reset();
        else if (accept)
            _stage = Stage.Sent;
    }

    // goes back as an offer the other way, their turn to answer
    public void Counter(int wager)
    {
        if (_stage != Stage.Offered)
            return;
        _wager = wager;
        _counters++;
        _stage = Stage.Sent;
        _send(w =>
        {
            w.Put((byte)MsgType.RaceCounter);
            w.Put(wager);
        });
    }

    public void OnStart(NetDataReader reader)
    {
        if (!_remotes.TryGetValue(reader.GetByte(), out _rival))
            return;
        _raceId = reader.GetInt();
        _wager = reader.GetInt();
        _second = reader.GetBool();
        Begin();
    }

    public void OnCancelled(string reason)
    {
        if (Racing)
            End(false, false, reason);
        else
            Reset();
        _notice(reason);
    }

    // the go names the moment both lights start, already on my clock. older servers send none and it's right away.
    // a clock that hasn't synced could be way off, so it never holds longer than a second
    public void OnGo(long atTicks)
    {
        _went = true;
        _goGot = DateTime.UtcNow.Ticks;
        _goAt = Math.Clamp(atTicks, _goGot, _goGot + TimeSpan.TicksPerSecond);
        if (_host == null || _stage != Stage.Staged || _restaging)
            ReleaseGo(_host != null ? _host.opponentCar_ : null);
    }

    private void ReleaseGo(RCC_CarControllerV3 opponent)
    {
        _go = true;
        if (opponent != null)
            opponent.racer_racerIsReady = true;
        _log.LogInfo($"Start lights released {(DateTime.UtcNow.Ticks - _goGot) / TimeSpan.TicksPerMillisecond} ms after the go came in");
    }

    public void OnResult(byte winner, int winnerMs, int loserMs)
    {
        _winner = winner;
        _winnerMs = winnerMs;
        _loserMs = loserMs;
    }

    // same call on both screens. my run already over = the game's own finish pays out, nothing to cut short
    public void OnEnded(byte winner, RaceEnd end)
    {
        if (!Racing)
            return;
        var contested = winner != Protocol.NoRacer;
        var won = winner == _myId;
        var line = EndLine(contested, won, end);
        _notice(line);
        if (_finishSent)
        {
            if (contested)
                _winner = winner;
            return;
        }
        End(contested, won, line);
    }

    private string EndLine(bool contested, bool won, RaceEnd end)
    {
        if (!contested)
            return end == RaceEnd.Stalled ? "The race was called off" : $"No contest, {(_forfeited ? "you" : RivalName)} backed out";
        return end switch
        {
            RaceEnd.Jumped => won ? $"{RivalName} jumped the start, you win" : "You jumped the start, race lost",
            RaceEnd.Left => won ? $"{RivalName} left, you win" : "You lost the race",
            _ => won ? $"{RivalName} quit, you win" : "You forfeited the race",
        };
    }

    // both back to the line. the jumper's game is already on its way there, mine gets sent the same road
    public void OnRestage(byte jumper)
    {
        if (_stage != Stage.Staged || _host == null)
            return;
        _notice(jumper == _myId ? "You jumped the start, back to the line" : $"{RivalName} jumped the start, back to the line");
        if (jumper == _myId || _restaging)
            return;
        _restaging = true;
        // that road costs rep and i did nothing wrong. with none the game skips it, comes back when i'm on the line
        var god = GodConstant.Instance;
        _heldRep = god.player_RepCurrentMeetSpot;
        god.player_RepCurrentMeetSpot = 0;
        _host.jumpedStart_ = true;
        _host.Phase = RacePhase.None;
    }

    // same rule as the server, it can't answer anymore
    public void OnLostServer()
    {
        if (!Racing)
        {
            Reset();
            return;
        }
        if (_rival == null || _rival.IsBot || _finishSent)
            return;
        End(_went, false, "Lost the server, race over");
        _notice("Lost the server, race over");
    }

    // ESC can't pause a race, the rival's clock wouldn't stop. twice = give up, server says what it costs
    public void OnEscape()
    {
        if (!HoldingPause)
            return;
        if (Time.unscaledTime >= _forfeitUntil)
        {
            _forfeitUntil = Time.unscaledTime + ForfeitSeconds;
            _notice("Press ESC again to forfeit the race");
            return;
        }
        _forfeited = true;
        if (_rival.IsBot)
        {
            End(true, false, "You forfeited the race");
            _notice("You forfeited the race");
            return;
        }
        Answer(false);
        _quitBy = Time.unscaledTime + QuitAnswerSeconds;
    }

    private bool HoldingPause => _host != null && _stage >= Stage.Staged && _host.Phase < RacePhase.PlayerFinished;

    // runs from Follow, the race might still be loading
    private void End(bool contested, bool won, string why)
    {
        _ending = true;
        _endContested = contested;
        _endWon = won;
        _endWhy = why;
    }

    // game's own way out. result known = raceRewards, like its pause menu quit. not known = race_cancel, money comes back
    private void Settle()
    {
        var host = _host;
        var god = GodConstant.Instance;
        _log.LogInfo($"Race ended here: contested {_endContested}, won {_endWon}, {_endWhy}");
        if (_endContested)
        {
            god.race_playerWonLastRace = _endWon;
            host.resultKnown_ = true;
        }
        var why = _endWhy;
        Reset();
        HideRaceUi(god);
        try
        {
            host.Fail(why);
        }
        catch (Exception e)
        {
            _log.LogError($"Could not end the race: {e}");
        }
    }

    // the normal finish fades this out, ending early never gets there
    private void HideRaceUi(GodConstant god)
    {
        var ui = god != null ? god.UI_Data : null;
        if (ui == null)
            return;
        ui.race_fadeRaceUI_Stop();
        ui.uiRace = false;
        if (ui.race_UICanvasGroup != null)
            ui.race_UICanvasGroup.alpha = 0f;
    }

    private void Begin()
    {
        try
        {
            Starting?.Invoke();
            var god = GodConstant.Instance;
            var spot = _spot();
            _character = Lender();
            if (god == null || spot == null || _character == null)
                throw new InvalidOperationException("No racer to lend a character");

            var car = _character.aiRacer_baseCar;
            Rename(_character);
            car.ai_characterParent = _character;
            var rep = _rival.Stats?.Rep ?? 0;
            var crew = _character.characterCrew;

            RememberRivalParts();
            _moneyAtStart = god.player_Money;
            _loanAtStart = god.player_bettingLoanMoney;
            spot.meetSpot_leave();
            god.race_assignDetails(car, rep, (RaceId)_raceId, _wager, false, GodConstant.Race_RaceType.cashBet, god.player_currentMeetspot, crew, false);
            // the game picks start spots from this, so one of us gets the npc's spot
            god.race_currentDetails.race_playerWasChallenged = _second;
            _playerName = god.player_Name;
            god.player_Name = _myTag();
            spot.LoadRace();

            _stage = Stage.Loading;
            _deadline = Time.unscaledTime + LoadTimeoutSeconds;
            _log.LogInfo($"Race {(RaceId)_raceId} for {_wager:N0} against {_rival.Tag}, second {_second}");
        }
        catch (Exception e)
        {
            _log.LogError($"Could not start the race: {e}");
            Abandon("The race could not start");
        }
    }

    // any racer with a car, the rival plays them under their own name
    public character_carPrefab Lender()
    {
        var god = GodConstant.Instance;
        if (god == null)
            return null;
        foreach (GodConstant.Race_Crews crew in Enum.GetValues(typeof(GodConstant.Race_Crews)))
        {
            var member = god.getTeamMember(crew, false);
            if (member != null && member.aiRacer_baseCar != null)
                return member;
        }
        return null;
    }

    public void Tick()
    {
        if (!Racing)
            return;
        try
        {
            Follow();
        }
        catch (Exception e)
        {
            _log.LogError($"Race tick failed: {e}");
            Abandon("The race broke, check the log");
        }
    }

    private void Follow()
    {
        if (_host == null)
        {
            if (_stage != Stage.Loading)
            {
                if (!_finishSent && !_forfeited && !_ending && !_rival.IsBot)
                    Answer(false);
                Reset();
                return;
            }
            if (Time.unscaledTime >= _deadline)
            {
                Abandon("The race did not load");
                return;
            }
            if (Time.unscaledTime < _nextFind)
                return;
            _nextFind = Time.unscaledTime + FindHostSeconds;
            _host = UnityEngine.Object.FindObjectOfType<RaceHost>();
            if (_host == null)
                return;
            _log.LogInfo($"Race host found in phase {_host.Phase}");
            _stage = Stage.Staged;
        }

        if (_quitBy > 0 && !_ending && Time.unscaledTime >= _quitBy)
            End(_went, false, "You forfeited the race");
        if (_ending)
        {
            Settle();
            return;
        }

        var phase = _host.Phase;
        NameOpponent();
        KeepRivalDrawn();
        if (HoldingPause)
        {
            GodConstant.Instance.no_pauseGame = true;
            _holdingPause = true;
        }
        if (_rival != null && !_rival.IsBot)
            FollowPlayer(phase);
        if (phase != _loggedPhase)
        {
            _loggedPhase = phase;
            LogRival(phase);
        }

        if (phase == RacePhase.Results && _resultsEnd == 0)
            _resultsEnd = Time.unscaledTime + ResultsSeconds;
        if (_resultsEnd > 0 && Time.unscaledTime >= _resultsEnd)
            Reset();
    }

    private void FollowPlayer(RacePhase phase)
    {
        var player = _host.playerCar_;
        var opponent = _host.opponentCar_;
        // the game drops the phase to None past the line and again before the results, so neither can be read off the phase
        var crossed = _host.playerFinished_ || phase >= RacePhase.PlayerFinished;
        if (phase >= RacePhase.Outro && !_outro)
        {
            _outro = true;
            LogPayout("Outro");
        }

        if (opponent != null)
            Hide(opponent, phase >= RacePhase.Racing);
        if (_outro)
            HoldOpponent(opponent);
        if (_outro || phase < RacePhase.Racing && !crossed)
            StandIn(opponent);
        else
            MakeRivalSolid();

        // a restart mid race would only exist on one screen
        _host.restartOfferMade_ = true;

        if (_stage == Stage.Staged)
        {
            WatchJump(opponent);
            if (!_restaging)
                HoldStart(player, opponent);
            if (phase == RacePhase.Racing)
            {
                _stage = Stage.Racing;
                _send(w => w.Put((byte)MsgType.RaceStarted));
                _log.LogInfo("Race started");
            }
        }
        if (_stage != Stage.Racing)
            return;

        if (!_outro)
            FollowRival(opponent);
        if (crossed && !_finishSent)
        {
            _finishSent = true;
            _log.LogInfo("Finished");
            _send(w => w.Put((byte)MsgType.RaceFinished));
        }
        ApplyResult(player, opponent);
        if (phase == RacePhase.Results)
        {
            if (!_paidLogged)
            {
                _paidLogged = true;
                LogPayout("Results");
            }
            PaintResults(player, opponent);
        }
    }

    // its colliders are off, the outro unfreezes it and it drops out of the world with my rival standing on it
    private static void HoldOpponent(RCC_CarControllerV3 opponent)
    {
        var body = opponent != null ? opponent.GetComponent<Rigidbody>() : null;
        if (body != null)
            body.isKinematic = true;
    }

    private void LogPayout(string when)
    {
        var god = GodConstant.Instance;
        if (god == null)
            return;
        var first = _host.finishOrder_ != null && _host.finishOrder_.Count > 0 ? _host.finishOrder_[0] : null;
        _log.LogInfo($"Payout at {when}: winner {_winner} me {_myId}, game won {_host.lastRoundWon_}, first {(first == null ? "nobody" : first == _host.playerCar_ ? "me" : "rival")}, "
            + $"wager {_wager:N0}, cash {_moneyAtStart:N0} > {god.player_Money:N0}, loan {_loanAtStart:N0} > {god.player_bettingLoanMoney:N0}, "
            + $"reward {god.race_rewardMoney:N0}, profit {god.race_rewardProfit:N0}, debt {god.race_rewardDebt:N0}");
    }

    // rival in the npc's place for every scene around the race
    private void StandIn(RCC_CarControllerV3 opponent)
    {
        var rival = RivalObject;
        if (opponent == null || rival == null)
            return;
        // rival sits right on the hidden car, solid they'd kick each other off the road
        if (_rival.Car != null && _ghosted != _rival.Car)
        {
            _ghosted = _rival.Car;
            foreach (var collider in _ghosted.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }
        rival.transform.SetPositionAndRotation(opponent.transform.position, opponent.transform.rotation);
    }

    private void MakeRivalSolid()
    {
        if (_ghosted != null)
            RemoteCars.SetCollisions(_ghosted);
        _ghosted = null;
    }

    private GameObject RivalObject => _rival.Car != null ? _rival.Car : _rival.Placeholder;

    // not every renderer on a car is on, combined meshes leave the originals off. so only the ones that were
    private void RememberRivalParts()
    {
        _drawn.Clear();
        _drawnCar = _rival.Car;
        if (_drawnCar == null)
            return;
        foreach (var renderer in _drawnCar.GetComponentsInChildren<Renderer>(false))
            if (renderer.enabled)
                _drawn.Add(renderer);
    }

    // the game clears the stage for a race, the rival is the one car that has to stay on it
    private void KeepRivalDrawn()
    {
        if (_rival.IsBot || Time.unscaledTime < _nextDrawn)
            return;
        _nextDrawn = Time.unscaledTime + FindHostSeconds;
        if (_drawnCar != _rival.Car)
            RememberRivalParts();
        var fixedUp = 0;
        foreach (var renderer in _drawn)
        {
            if (renderer != null && !renderer.enabled)
            {
                renderer.enabled = true;
                fixedUp++;
            }
        }
        if (fixedUp > 0)
            _log.LogInfo($"Switched {fixedUp} rival renderers back on in {_loggedPhase}");
    }

    // only a log, must never touch the race
    private void LogRival(RacePhase phase)
    {
        try
        {
            var car = _rival.Car;
            var shadow = _rival.Placeholder;
            var scene = SceneId.FromName(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            var line = $"Rival in {phase}: data {_rival.Interp.HasData}, scene {_rival.Interp.Scene} here {scene}, placeholder {(shadow != null ? shadow.activeInHierarchy : false)}";
            if (car == null)
            {
                _log.LogInfo($"{line}, no car");
                return;
            }
            var total = 0;
            var on = 0;
            foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            {
                total++;
                if (renderer.enabled)
                    on++;
            }
            var opponent = _host.opponentCar_;
            var parent = car.transform.parent;
            _log.LogInfo($"{line}, car active {car.activeSelf}/{car.activeInHierarchy}, parent {(parent != null ? parent.name : "none")}, layer {car.layer}, renderers {on}/{total}, broken {RemoteBuilds.Broken(car)}, "
                + $"at {car.transform.position}, npc at {(opponent != null ? opponent.transform.position : default)}, me at {(_host.playerCar_ != null ? _host.playerCar_.transform.position : default)}");
        }
        catch (Exception e)
        {
            _log.LogWarning($"Rival log failed: {e.Message}");
        }
    }

    // the car the game spawned has its own character, and that's the name on the results
    private void NameOpponent()
    {
        var opponent = _host.opponentCar_;
        Rename(_host.opponentCharacter_);
        if (_named || opponent == null)
            return;
        _named = true;
        var custom = opponent.GetComponent<CarLocalCustom>();
        if (custom != null)
            Rename(custom.ai_characterParent);
    }

    private void Rename(character_carPrefab character)
    {
        if (character == null || character.aiRacer_characterName == _rival.Tag)
            return;
        _renamed.Add((character, character.aiRacer_characterName));
        character.aiRacer_characterName = _rival.Tag;
    }

    // my jump goes to the server once. game clears the flag when it's back on the line, then it's a fresh countdown
    private void WatchJump(RCC_CarControllerV3 opponent)
    {
        if (_host.jumpedStart_)
        {
            if (!_restaging)
            {
                _restaging = true;
                _log.LogInfo("Jumped the start");
                _send(w => w.Put((byte)MsgType.RaceJumped));
            }
            if (opponent != null)
                opponent.racer_racerIsReady = true;
            return;
        }
        if (!_restaging)
            return;
        _restaging = false;
        _go = _readySent = _coroutinesStopped = false;
        _goAt = 0;
        GiveRepBack();
    }

    private void GiveRepBack()
    {
        var god = GodConstant.Instance;
        if (_heldRep is int rep && god != null)
            god.player_RepCurrentMeetSpot = rep;
        _heldRep = null;
    }

    // game waits for both to flash lights, keep the rival's flag down till the server says go
    private void HoldStart(RCC_CarControllerV3 player, RCC_CarControllerV3 opponent)
    {
        if (player == null || opponent == null)
            return;
        if (!_go && _goAt > 0 && DateTime.UtcNow.Ticks >= _goAt)
            ReleaseGo(opponent);
        if (!_go)
        {
            if (player.racer_racerReadyUp && !_coroutinesStopped)
            {
                opponent.StopAllCoroutines();
                _coroutinesStopped = true;
            }
            opponent.racer_racerIsReady = false;
        }
        if (player.racer_racerIsReady && !_readySent)
        {
            _readySent = true;
            _send(w => w.Put((byte)MsgType.RaceReady));
        }
    }

    // hidden car sits on the rival's. the AI is off so its route tracker stands still, and the game reads positions from that.
    // so i feed it here, same search as the AI uses. wider and it can jump to the next hairpin leg
    private void FollowRival(RCC_CarControllerV3 opponent)
    {
        var ai = _host.opponentAi_;
        if (opponent == null || ai == null)
            return;
        ai.enabled = false;
        var body = opponent.GetComponent<Rigidbody>();
        if (body != null)
            body.isKinematic = true;
        var rival = RivalObject;
        if (rival != null)
            opponent.transform.SetPositionAndRotation(rival.transform.position, rival.transform.rotation);
        if (_trackingFailed)
            return;
        try
        {
            var position = opponent.transform.position;
            var moved = _rivalWas is Vector3 was ? Vector3.Distance(was, position) : 0f;
            _rivalWas = position;
            ai.ctx_.Tracker.Update(position, Mathf.Min(moved * 2f + RouteSearchMeters, MaxRouteSearchMeters), Time.deltaTime);
            if (Time.unscaledTime < _nextPositionLog)
                return;
            _nextPositionLog = Time.unscaledTime + PositionLogSeconds;
            _log.LogInfo($"Race position {_host.session_.PlayerPosition}, gap {_host.session_.Gap:0} m, rival at {ai.Progress:0}");
        }
        catch (Exception e)
        {
            _trackingFailed = true;
            _log.LogWarning($"Race positions stay off: {e.Message}");
        }
    }

    // the game reads the win off the finish list twice and only keeps the flag it gets from that. the side that crossed first
    // has itself on top no matter what, so the server's word goes in both
    private void ApplyResult(RCC_CarControllerV3 player, RCC_CarControllerV3 opponent)
    {
        if (_winner < 0 || player == null || opponent == null)
            return;
        var won = _winner == _myId;
        ShowTime(player, won ? _winnerMs : _loserMs);
        ShowTime(opponent, won ? _loserMs : _winnerMs);
        if (!won && !_finishSent && !_opponentFinished && !_forfeited)
        {
            _opponentFinished = true;
            _host.session_.OnOpponentFinished();
            _notice($"{RivalName} won, finish the run");
        }
        if (!_finishSent)
            return;

        if (!_resultLogged)
        {
            _resultLogged = true;
            _log.LogInfo($"Race result: {(won ? "won" : "lost")}, {_winnerMs} ms vs {_loserMs} ms, game had won {_host.lastRoundWon_}");
            if (_outro && _host.lastRoundWon_ != won)
                _log.LogWarning("Race result came after the outro started, the game pays its own call");
        }
        _host.lastRoundWon_ = won;
        var order = _host.finishOrder_;
        var first = won ? player : opponent;
        if (order == null || order.Count > 0 && order[0] == first)
            return;
        order.Remove(first);
        order.Insert(0, first);
    }

    private static void ShowTime(RCC_CarControllerV3 car, int ms)
    {
        if (ms < 0)
            return;
        car.overwrite_time = true;
        car.timeText = Clock(ms);
    }

    private static string Clock(int ms) => $"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms / 10 % 100:00}";

    // game fills these in once and writes DNF for whoever's still driving, the loser's time can land later
    private void PaintResults(RCC_CarControllerV3 player, RCC_CarControllerV3 opponent)
    {
        if (_winner < 0 || Time.unscaledTime < _nextPaint)
            return;
        _nextPaint = Time.unscaledTime + FindHostSeconds;
        _menu ??= GodConstant.Instance.spawnedUI;
        var order = _host.finishOrder_;
        if (_menu == null || order == null)
            return;
        var slots = _menu.race_racerResultsSlots;
        var won = _winner == _myId;
        for (var i = 0; i < order.Count && i < slots.Length; i++)
        {
            var mine = order[i] == player;
            var ms = mine == won ? _winnerMs : _loserMs;
            if (ms >= 0 && (mine || order[i] == opponent))
                slots[i].racer_Time.SetText(Clock(ms));
        }
    }

    private static void Hide(RCC_CarControllerV3 car, bool colliders)
    {
        foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        if (colliders)
            foreach (var collider in car.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        // the rival's own car makes the noise now
        foreach (var audio in car.GetComponentsInChildren<AudioSource>(true))
            audio.mute = true;
    }

    private void Abandon(string reason)
    {
        _notice(reason);
        if (!_rival.IsBot)
            Answer(false);
        if (_host != null && !_ending)
            End(_went, false, reason);
        else
            Reset();
    }

    private void Reset()
    {
        MakeRivalSolid();
        foreach (var (character, name) in _renamed)
            if (character != null)
                character.aiRacer_characterName = name;
        _renamed.Clear();
        var god = GodConstant.Instance;
        if (god != null && _playerName != null)
            god.player_Name = _playerName;
        if (god != null && _holdingPause)
            god.no_pauseGame = false;
        GiveRepBack();
        _playerName = null;
        _character = null;
        _menu = null;
        _drawn.Clear();
        _drawnCar = null;
        _loggedPhase = null;
        _nextDrawn = 0;
        _rivalWas = null;
        _nextPositionLog = 0;
        _stage = Stage.None;
        _rival = null;
        _host = null;
        _go = _readySent = _named = _forfeited = _coroutinesStopped = _finishSent = _opponentFinished = _trackingFailed = false;
        _went = _restaging = _holdingPause = _ending = _endContested = _endWon = _outro = _resultLogged = _paidLogged = false;
        _endWhy = null;
        _goAt = _goGot = 0;
        _quitBy = 0;
        _winner = _winnerMs = _loserMs = -1;
        _resultsEnd = _forfeitUntil = _nextPaint = 0;
        _counters = 0;
    }
}
