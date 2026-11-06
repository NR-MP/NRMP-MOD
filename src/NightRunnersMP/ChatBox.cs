using System;
using System.Collections.Generic;
using BepInEx.Logging;
using NightRunnersMP.Shared;
using Rewired;
using UnityEngine;

namespace NightRunnersMP;

// samp chat: lines top left, coloured names, T to type. game keys off while typing so you can't drive into a wall
internal sealed class ChatBox
{
    private const KeyCode TypeKey = KeyCode.T;
    private const int MaxLines = 100;
    private const int LinesShown = 10;
    private const int WrapAt = 90;
    private const float DesignHeight = 1080f;
    private const float Left = 16f;
    private const float Top = 44f;
    private const float RowHeight = 24f;
    private const float PictureSize = 20f;
    private const float InputWidth = 760f;
    private const float InputHeight = 30f;
    private const int FontSize = 19;
    // quiet this long and the lines fade out, a new line or T brings them back
    private const float ShowSeconds = 12f;
    private const float FadeSeconds = 2f;

    private static readonly Color NoticeColor = new(1f, 0.85f, 0.4f);
    private static readonly Color CrewColor = new(0.45f, 1f, 0.55f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.9f);
    private static readonly Color[] Names =
    {
        new(1f, 0.35f, 0.35f), new(0.4f, 0.7f, 1f), new(0.45f, 1f, 0.45f), new(1f, 0.8f, 0.3f),
        new(1f, 0.5f, 1f), new(0.35f, 1f, 1f), new(1f, 0.6f, 0.25f), new(0.75f, 0.6f, 1f),
    };

    private readonly ManualLogSource _log;
    private readonly Action<string> _send;
    private readonly List<Line> _lines = new();
    private GUIStyle _style;
    private Texture2D _inputBack;
    private string _draft = "";
    private int _scroll;
    private bool _skipLetter;
    private float _shownAt = -ShowSeconds - FadeSeconds;
    private float _alpha = 1f;

    public bool IsTyping { get; private set; }
    // hide UI is on, T still opens the box
    public bool Muted { get; set; }

    public ChatBox(ManualLogSource log, Action<string> send)
    {
        _log = log;
        _send = send;
    }

    public void Add(string from, int slot, Texture2D picture, bool hasPicture, string text) =>
        AddLine(from, Names[slot % Names.Length], picture, hasPicture, text);

    public void AddCrew(string from, Texture2D picture, bool hasPicture, string text) =>
        AddLine($"(CREW) {from}", CrewColor, picture, hasPicture, text);

    public void AddNotice(string text) => AddLine("", NoticeColor, null, false, text);

    private void AddLine(string from, Color color, Texture2D picture, bool hasPicture, string text)
    {
        _log.LogInfo(from.Length > 0 ? $"Chat {from}: {text}" : $"Chat {text}");
        var first = true;
        foreach (var part in Wrap(text))
        {
            _lines.Add(new Line
            {
                From = first ? from : "",
                // wrapped lines have no name, so they'd get drawn in the name colour. only notices keep theirs
                Color = first || from.Length == 0 ? color : Color.white,
                Picture = first ? picture : null,
                HasPicture = hasPicture,
                Text = part,
            });
            first = false;
        }
        if (_lines.Count > MaxLines)
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        _scroll = 0;
        _shownAt = Time.unscaledTime;
    }

    private static IEnumerable<string> Wrap(string text)
    {
        while (text.Length > WrapAt)
        {
            var cut = text.LastIndexOf(' ', WrapAt);
            if (cut <= 0)
                cut = WrapAt;
            yield return text.Substring(0, cut);
            text = text.Substring(cut).TrimStart();
        }
        yield return text;
    }

    public void Draw(bool connected, bool blocked)
    {
        if (!connected)
        {
            if (IsTyping)
                SetTyping(false);
            return;
        }
        var e = Event.current;
        if (IsTyping && e.type == EventType.KeyDown)
        {
            HandleKey(e);
            e.Use();
        }
        else if (!IsTyping && !blocked && e.type == EventType.KeyDown && e.keyCode == TypeKey)
        {
            SetTyping(true);
            _skipLetter = true;
            e.Use();
        }
        if (e.type != EventType.Repaint)
            return;
        if (IsTyping)
            _shownAt = Time.unscaledTime;
        _alpha = Mathf.Clamp01(1f - (Time.unscaledTime - _shownAt - ShowSeconds) / FadeSeconds);
        if (_alpha <= 0f || (Muted && !IsTyping))
            return;

        var scale = Screen.height / DesignHeight;
        if (_style == null || _style.font != Avatars.TagFont)
            _style = new GUIStyle { font = Avatars.TagFont, fontSize = Mathf.RoundToInt(FontSize * scale), clipping = TextClipping.Overflow };
        _style.normal.textColor = Color.white;

        var last = _lines.Count - _scroll;
        var first = Math.Max(0, last - LinesShown);
        for (var i = first; i < last; i++)
            DrawLine(_lines[i], Left * scale, (Top + (i - first) * RowHeight) * scale, scale);
        if (IsTyping)
            DrawInput(scale);
    }

    private void DrawLine(Line line, float x, float y, float scale)
    {
        if (line.Picture != null)
        {
            var saved = GUI.color;
            GUI.color = Faded(Color.white);
            GUI.DrawTexture(new Rect(x, y, PictureSize * scale, PictureSize * scale), line.Picture);
            GUI.color = saved;
            x += (PictureSize + 6f) * scale;
        }
        if (line.From.Length == 0)
        {
            Text(x, y, line.Text, line.Color);
            return;
        }
        var name = $"{line.From}: ";
        Text(x, y, name, line.Color);
        Text(x + _style.CalcSize(new GUIContent(name)).x, y, line.Text, Color.white);
    }

    private void DrawInput(float scale)
    {
        var y = (Top + LinesShown * RowHeight + 8f) * scale;
        if (_inputBack == null)
        {
            _inputBack = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _inputBack.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
            _inputBack.Apply();
        }
        GUI.DrawTexture(new Rect(Left * scale, y, InputWidth * scale, InputHeight * scale), _inputBack);
        Text((Left + 8f) * scale, y + 3f * scale, $"Say: {_draft}_", Color.white);
    }

    private void Text(float x, float y, string text, Color color)
    {
        var size = _style.CalcSize(new GUIContent(text));
        var saved = GUI.color;
        GUI.color = Faded(Shadow);
        GUI.Label(new Rect(x + 1, y + 1, size.x, size.y), text, _style);
        GUI.Label(new Rect(x - 1, y - 1, size.x, size.y), text, _style);
        GUI.color = Faded(color);
        GUI.Label(new Rect(x, y, size.x, size.y), text, _style);
        GUI.color = saved;
    }

    private Color Faded(Color color) => new(color.r, color.g, color.b, color.a * _alpha);

    private void HandleKey(Event e)
    {
        // the key that opened the box comes through again as a letter
        var skip = _skipLetter;
        _skipLetter = false;
        if (skip && e.keyCode == KeyCode.None && char.ToUpperInvariant(e.character) == 'T')
            return;

        switch (e.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (_draft.Trim().Length > 0)
                    _send(_draft.Trim());
                SetTyping(false);
                break;
            case KeyCode.Escape:
                SetTyping(false);
                break;
            case KeyCode.PageUp:
                _scroll = Math.Min(_scroll + LinesShown / 2, Math.Max(0, _lines.Count - LinesShown));
                break;
            case KeyCode.PageDown:
                _scroll = Math.Max(0, _scroll - LinesShown / 2);
                break;
            case KeyCode.Backspace:
                if (_draft.Length > 0)
                    _draft = _draft.Substring(0, _draft.Length - 1);
                break;
            default:
                if (!char.IsControl(e.character) && _draft.Length < Protocol.MaxChatLength)
                    _draft += e.character;
                break;
        }
    }

    private void SetTyping(bool typing)
    {
        IsTyping = typing;
        _draft = "";
        _scroll = 0;
        if (ReInput.isReady)
            ReInput.controllers.Keyboard.enabled = !typing;
    }

    private sealed class Line
    {
        public string From;
        public Color Color;
        public Texture2D Picture;
        public bool HasPicture;
        public string Text;
    }
}
