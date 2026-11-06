using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PlanetJem.GameSettings;
using PlanetJem.Input;
using PlanetJem.Localization;
using GameLocale = PlanetJem.Localization.Locale;
using PlanetJem.Phone;
using PlanetJem.UI.Hints;
using UnityEngine;

namespace NightRunnersMP;

// PA challenge on the game's real phone call: calling, ring, silhouette, propose, accept/reject, select amount.
// BeginCallCore minus the script, so i drive the session from here and the phone does the rest. each side only answers the other's offer.
// a dev bot makes the first offer itself so the whole thing can be tested alone
internal sealed class RaceCall
{
    private const float AnswerSeconds = 30f;
    private const float RingAndAnswerSeconds = 45f;
    private const float LineSeconds = 1.6f;
    private const int FirstWager = 1000;
    private const int FpsCallMode = 2;
    private const int BotOfferShare = 4;

    private enum Step { Idle, Ringing, Talking, Offer, Counter, Waiting, Ending }

    private readonly ManualLogSource _log;
    private readonly PlayerRace _race;
    private readonly Func<string> _myTag;

    private UIHintsDisplay _hints;
    private UIHintAction _hint;
    private Step _step;
    private PhoneController _phone;
    private PhoneController.CallRun _run;
    private PhoneCallSession _session;
    private character_carPrefab _caller;
    private string _callerName;
    private Remote _aimed;
    private Remote _rival;
    private byte _myId;
    private bool _outgoing;
    private int _roadIndex;
    private int _wager;
    private float _deadline;

    // one thing in flight at a time, either a line or an open prompt
    private UniTask _task;
    private Action _then;
    private PhoneController.CallPromptKind _askedKind;
    private PhoneController.CallPrompt _prompt;
    private Action<PhoneController.CallPrompt> _answered;

    public RaceCall(ManualLogSource log, PlayerRace race, Func<string> myTag)
    {
        _log = log;
        _race = race;
        _myTag = myTag;
        _race.Starting = Close;
    }

    // first use, game UI isn't up when the mod starts
    private UIHintsDisplay Hints => _hints ??= new UIHintsDisplay();

    private string RivalName => _rival?.Tag ?? _race.RivalName;

    public void Tick(Parking parking, ushort scene, byte myId, IEnumerable<Remote> remotes, bool blocked)
    {
        try
        {
            _myId = myId;
            if (_step != Step.Idle)
                Run();
            else if (!blocked)
                Idle(parking, scene, remotes);
            else
                Aim(null);
        }
        catch (Exception e)
        {
            _log.LogError($"Race call failed: {e}");
            if (_race.Busy && !_race.Racing)
                _race.Answer(false);
            Close();
        }
    }

    private void Idle(Parking parking, ushort scene, IEnumerable<Remote> remotes)
    {
        var onFoot = parking.Inside && parking.TryReadWalker(out _, out _) && !PhoneController.IsCallBusy;
        if (_race.HasOffer)
        {
            Aim(null);
            if (onFoot)
                Dial(_race.Find(_race.RivalId), false);
            else
                _race.Answer(false);
            return;
        }
        var rival = onFoot ? PlayerCards.Aimed(remotes) : null;
        Aim(rival != null && _race.CanChallenge(rival, scene) ? rival : null);
        if (_aimed == null || !GameInput.Released(InputActionId.WalkConfirm))
            return;
        var target = _aimed;
        Aim(null);
        Dial(target, true);
    }

    private void Aim(Remote rival)
    {
        if (rival == _aimed)
            return;
        _aimed = rival;
        if (rival != null)
        {
            _hint = new UIHintAction($"RACE {rival.Tag}", InputActionId.WalkConfirm, InputTrigger.Released);
            ShowHint(Hints, _hint);
        }
        else
        {
            _hint = null;
            _hints?.Hide();
        }
    }

    // the Show wrapper needs an Il2CppStructArray, which UIHintAction can't be (it holds strings), so the array gets built and passed by hand.
    // _hint keeps the strings alive while the game shows it
    private static unsafe void ShowHint(UIHintsDisplay display, UIHintAction action)
    {
        const int firstElement = 0x20;
        var type = Il2CppClassPointerStore<UIHintAction>.NativeClassPtr;
        uint align = 0;
        var size = IL2CPP.il2cpp_class_value_size(type, ref align);
        var array = IL2CPP.il2cpp_array_new(type, 1);
        Buffer.MemoryCopy((void*)IL2CPP.il2cpp_object_unbox(action.Pointer), (byte*)array + firstElement, size, size);

        var show = IL2CPP.GetIl2CppMethod(Il2CppClassPointerStore<UIHintsDisplay>.NativeClassPtr, false, nameof(UIHintsDisplay.Show),
            "System.Void", "PlanetJem.UI.Hints.UIHintAction[]");
        var args = stackalloc IntPtr[1];
        args[0] = array;
        var error = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(show, display.Pointer, (void**)args, ref error);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(error);
    }

    private void Dial(Remote rival, bool outgoing)
    {
        if (rival == null)
            throw new InvalidOperationException("Rival left");
        if (!PhoneController.TryGetInstance(out _phone, nameof(RaceCall)))
            throw new InvalidOperationException("No phone");

        _rival = rival;
        _outgoing = outgoing;
        _caller = _race.Lender() ?? throw new InvalidOperationException("No racer to lend a character");
        _callerName = _caller.aiRacer_characterName;
        _caller.aiRacer_characterName = rival.Tag;
        if (!_phone.TryCreateRun(PhoneCallType.MeetSpotBetting, _caller, null, outgoing, out _run))
            throw new InvalidOperationException("The phone can't call right now");

        _phone.pendingCall_ = null;
        _phone.currentCall_ = _run;
        _run.PortraitLoaded = PhoneController.LoadPortraitAsync(_run);
        _phone.dialog_.ClearLines();
        if (outgoing)
            FpsModes.Apply((FpsMode)FpsCallMode);
        _phone.RaiseForCall();
        _run.PickUpTimer = 0f;

        _session = new PhoneCallSession(PhoneCallType.MeetSpotBetting, _caller, null, outgoing, !outgoing, _run.SessionToken);
        _run.Session = _session;
        _session.Bind(_phone, _run);
        if (outgoing)
            _phone.RingOutgoingAsync(_run, false);
        else
            _phone.RingIncomingAsync(_run);
        _step = Step.Ringing;
        _log.LogInfo($"Race call {(outgoing ? "to" : "from")} {rival.Tag}");
    }

    private void Run()
    {
        if (_run.TornDown || _phone.currentCall_?.Pointer != _run.Pointer)
        {
            if (_race.Busy && !_race.Racing)
                _race.Answer(false);
            Finish();
            return;
        }
        if (_step == Step.Ending)
            return;
        if (_step == Step.Ringing && _run.PickedUp)
        {
            _step = Step.Talking;
            if (_outgoing)
                AskRoad();
            else
                ShowOffer();
            return;
        }

        var waitingOnRival = _step is Step.Offer or Step.Counter or Step.Waiting;
        if (waitingOnRival && Time.unscaledTime >= _deadline)
        {
            Decline();
            return;
        }
        if (waitingOnRival && !_rival.IsBot && !_race.Busy)
        {
            ClosePrompt();
            RivalLeft();
            return;
        }
        if (_step == Step.Waiting && _race.HasOffer)
        {
            _step = Step.Talking;
            After(_session.SayReplyAsync(Key(GameLocale.phone_chat_rejects_offer, RivalName)), ShowOffer);
            return;
        }

        if (_then != null)
        {
            if (!_task.GetAwaiter().IsCompleted)
                return;
            var then = _then;
            _then = null;
            then();
        }
        else if (_answered != null)
        {
            WaitAnswer();
        }
    }

    private void After(UniTask task, Action then)
    {
        _task = task;
        _then = then;
    }

    private void Asked(PhoneController.CallPromptKind kind, Action<PhoneController.CallPrompt> answered)
    {
        _askedKind = kind;
        _prompt = null;
        _answered = answered;
    }

    // the Async calls hand back UniTask<T>, the prompt's own completion is simpler to poll and is what they read anyway
    private void WaitAnswer()
    {
        if (_prompt == null)
        {
            var open = _phone.callPrompt_;
            if (open != null && open.Kind == _askedKind)
                _prompt = open;
            return;
        }
        if (_prompt.Completion.UnsafeGetStatus() == UniTaskStatus.Pending)
            return;
        var answered = _answered;
        var prompt = _prompt;
        _answered = null;
        _prompt = null;
        answered(prompt);
    }

    private void ClosePrompt()
    {
        _answered = null;
        _prompt = null;
        _then = null;
    }

    private void AskRoad()
    {
        var names = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var race in PlayerRace.Races)
            names.Add(race.Name);
        _ = _session.PickAsync(new PhonePickQuestion
        {
            Options = names.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(),
            Index = 0,
        });
        Asked(PhoneController.CallPromptKind.Pick, prompt =>
        {
            _roadIndex = prompt.Completion.GetResult(0);
            if (_rival.IsBot)
            {
                _wager = Mathf.Max(BettingNegotiation.GetOfferStep(_race.Money), _race.Money / BotOfferShare);
                ShowOffer();
            }
            else
            {
                _wager = Mathf.Min(FirstWager, _race.Money);
                AskAmount(SendChallenge, HangUp);
            }
        });
    }

    private void AskAmount(Action send, Action exit)
    {
        _ = _session.AskAmountAsync(new PhoneAmountQuestion
        {
            Value = _wager,
            Min = Mathf.Min(BettingNegotiation.GetOfferStep(_wager), _race.Money),
            Max = _race.Money,
            Step = BettingNegotiation.GetOfferStep(_wager),
            StressRate = _outgoing && !_race.Busy ? 0f : 1f / AnswerSeconds,
        });
        Asked(PhoneController.CallPromptKind.Amount, prompt =>
        {
            if (prompt.Exited)
            {
                exit();
                return;
            }
            _wager = prompt.Completion.GetResult(0);
            send();
        });
    }

    private void SendChallenge()
    {
        After(_session.PlayerSayAsync(Proposes(_myTag(), _wager)), () =>
        {
            _race.Challenge(_rival, PlayerRace.Races[_roadIndex].Id, _wager, _myId);
            Wait();
        });
    }

    private void ShowOffer()
    {
        var wager = _rival.IsBot ? _wager : _race.Wager;
        var line = Proposes(RivalName, wager);
        if (!_outgoing)
            line += $" - {_race.RaceName}";
        After(_session.SayAsync(line), () =>
        {
            _step = Step.Offer;
            _deadline = Time.unscaledTime + AnswerSeconds;
            _ = _session.AskAsync(new PhoneQuestion { YesEnabled = wager <= _race.Money, StressRate = 1f / AnswerSeconds });
            Asked(PhoneController.CallPromptKind.YesNo, prompt =>
            {
                // yes closes the prompt with 0, no with 1
                if (prompt.Completion.GetResult(0) == 0)
                    Accept();
                else
                    Reject();
            });
        });
    }

    private void Accept()
    {
        _step = Step.Talking;
        After(_session.PlayerAnswerAsync(Key(GameLocale.phone_chat_accepts_offer, _myTag())), () =>
        {
            if (_rival.IsBot)
            {
                _race.Challenge(_rival, PlayerRace.Races[_roadIndex].Id, _wager, _myId);
                return;
            }
            _race.Answer(true);
            Wait();
        });
    }

    private void Reject()
    {
        _step = Step.Talking;
        After(_session.PlayerAnswerAsync(Key(GameLocale.phone_chat_rejects_offer, _myTag())), () =>
        {
            if (!_rival.IsBot && !_race.CanCounter)
            {
                Decline();
                return;
            }
            _step = Step.Counter;
            _deadline = Time.unscaledTime + AnswerSeconds;
            _wager = Mathf.Min(_rival.IsBot ? _wager : _race.Wager, _race.Money);
            AskAmount(SendCounter, Decline);
        });
    }

    private void SendCounter()
    {
        _step = Step.Talking;
        After(_session.PlayerSayAsync(Proposes(_myTag(), _wager)), () =>
        {
            if (_rival.IsBot)
            {
                After(_session.SayReplyAsync(Key(GameLocale.phone_chat_accepts_offer, RivalName)), () =>
                    After(_session.DelayAsync(LineSeconds), () =>
                        _race.Challenge(_rival, PlayerRace.Races[_roadIndex].Id, _wager, _myId)));
                return;
            }
            _race.Counter(_wager);
            Wait();
        });
    }

    private void Wait()
    {
        if (_step == Step.Idle)
            return;
        _step = Step.Waiting;
        _deadline = Time.unscaledTime + RingAndAnswerSeconds;
    }

    private void RivalLeft()
    {
        _step = Step.Talking;
        After(_session.SayReplyAsync(Key(GameLocale.phone_chat_rejects_offer, RivalName)), () =>
            After(_session.DelayAsync(LineSeconds), HangUp));
    }

    private void Decline()
    {
        ClosePrompt();
        if (_race.Busy && !_race.Racing)
            _race.Answer(false);
        HangUp();
    }

    private void HangUp()
    {
        _step = Step.Ending;
        _session.HangUp();
    }

    private static string Proposes(string name, int wager) =>
        Key(GameLocale.phone_chat_proposes_wager, name, GameLocale.FormatMoney(wager));

    private static string Key(LocalizationKey key, params string[] values)
    {
        var args = new Il2CppReferenceArray<Il2CppSystem.Object>(values.Length);
        for (var i = 0; i < values.Length; i++)
            args[i] = new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(values[i]));
        return key.Format(args);
    }

    // the race is about to take over, or something broke
    private void Close()
    {
        if (_step == Step.Idle)
            return;
        var session = _session;
        var ended = _run == null || _run.TornDown;
        Finish();
        if (!ended)
            session?.HangUp();
    }

    private void Finish()
    {
        _step = Step.Idle;
        ClosePrompt();
        if (_caller != null)
            _caller.aiRacer_characterName = _callerName;
        _caller = null;
        _rival = null;
        _session = null;
        _run = null;
        _phone = null;
    }
}
