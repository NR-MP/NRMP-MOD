using System.Collections.Generic;
using BepInEx.Logging;
using Rewired;
using UnityEngine;

namespace NightRunnersMP;

// U hides the game's HUD and ours, for clean shots. Esc, T or F7 bring it back so a menu never opens invisible
internal sealed class HideUi
{
    public const KeyCode Key = KeyCode.U;
    private const float RescanEvery = 0.5f;

    private readonly ManualLogSource _log;
    private readonly List<Canvas> _hidden = new();
    private float _scanAt;
    private bool _checked;

    public bool Hidden { get; private set; }

    public HideUi(ManualLogSource log) => _log = log;

    public void Tick(bool blocked)
    {
        if (!_checked && ReInput.isReady)
        {
            _checked = true;
            WarnIfGameUses(_log, Key);
        }
        if (Hidden && blocked)
            Show();
        // the game opens new hud bits as you drive, catch those too
        if (Hidden && Time.unscaledTime >= _scanAt)
            Scan();
    }

    // from OnGUI, same as the other keys
    public void OnKey(KeyCode key, bool blocked)
    {
        if (Hidden && (key == Key || key == KeyCode.Escape || key == KeyCode.F7))
            Show();
        else if (!Hidden && key == Key && !blocked)
            Hide();
    }

    private void Hide()
    {
        Hidden = true;
        Scan();
        _log.LogInfo($"UI hidden, {_hidden.Count} canvases");
    }

    private void Show()
    {
        Hidden = false;
        foreach (var canvas in _hidden)
            if (canvas != null)
                canvas.enabled = true;
        _hidden.Clear();
    }

    // screen canvases only, world ones are signs and boards in the map
    private void Scan()
    {
        _scanAt = Time.unscaledTime + RescanEvery;
        foreach (var canvas in Object.FindObjectsOfType<Canvas>())
        {
            if (!canvas.enabled || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace)
                continue;
            canvas.enabled = false;
            _hidden.Add(canvas);
        }
    }

    // the game reads keys through Rewired, say so in the log if it has U for something too
    public static void WarnIfGameUses(ManualLogSource log, KeyCode key)
    {
        try
        {
            for (var i = 0; i < ReInput.players.playerCount; i++)
            foreach (var map in List(ReInput.players.GetPlayer(i).controllers.maps.GetMaps(ControllerType.Keyboard, 0)))
            foreach (var element in List(map.AllMaps))
            {
                if (element.keyCode != key)
                    continue;
                var action = ReInput.mapping.GetAction(element.actionId);
                log.LogWarning($"{key} is also the game's {action?.name ?? element.actionId.ToString()} key");
            }
        }
        catch (System.Exception e)
        {
            log.LogWarning($"Couldn't read the game's keys: {e.Message}");
        }
    }

    // il2cpp lists don't foreach
    private static IEnumerable<T> List<T>(Il2CppSystem.Collections.Generic.IList<T> list)
    {
        var real = list?.TryCast<Il2CppSystem.Collections.Generic.List<T>>();
        if (real == null)
            yield break;
        for (var i = 0; i < real.Count; i++)
            yield return real[i];
    }
}
