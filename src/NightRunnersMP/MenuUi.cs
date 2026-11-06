using System;
using System.IO;
using UnityEngine;

namespace NightRunnersMP;

// F7 look: dark panels, one red accent, game's card font. art in the ui folder (street racing kit), nine sliced by Border.
// all coords in 1280x720, MultiplayerMenu scales it
internal sealed class MenuUi
{
    public const float DesignWidth = 1280f;
    public const float DesignHeight = 720f;
    public static readonly Rect Panel = new(140, 36, 1000, 648);
    public static readonly Rect List = new(Panel.x + 20, Panel.y + 122, 960, 364);
    public static readonly Rect Status = new(List.x, List.yMax + 4, List.width, 58);

    private const int Border = 14;
    private const float RowInset = 14f;
    private const float NumberWidth = 44f;
    private const float RightWidth = 112f;
    private const float CountWidth = 150f;
    private const float KeyGap = 10f;
    private const float KeyHeight = 56f;

    private static readonly Color Accent = new(0.749f, 0.063f, 0.063f);
    private static readonly Color Deep = new(0.09f, 0.09f, 0.098f);
    private static readonly Color Rule = new(0.22f, 0.22f, 0.235f);
    private static readonly Color Band = new(0.14f, 0.14f, 0.149f);
    private static readonly Color Surface = new(0.035f, 0.035f, 0.039f);
    private static readonly Color Ink = new(0.92f, 0.92f, 0.93f);
    private static readonly Color Muted = new(0.6f, 0.6f, 0.63f);
    private static readonly Color White = new(0.97f, 0.97f, 0.97f);
    private static readonly Color Dim = new(0.38f, 0.38f, 0.4f);

    private readonly GUIStyle _panel = Box("panel", Surface);
    private readonly GUIStyle _button = Box("button", Band);
    private readonly GUIStyle _buttonHover = Box("button_hover", Accent);
    private readonly GUIStyle _row = Box("row", Band);
    private readonly GUIStyle _rowHover = Box("row_hover", Accent);
    private readonly GUIStyle _input = Box("input", Surface);
    private readonly Texture2D _logo = Art("logo");
    private readonly string[] _splashes = Splashes();
    private int _splash;
    private float _lastFrame = -10f;
    private GUIStyle _splashText;
    private GUIStyle _splashShadow;
    private readonly Texture2D _solid = Flat(Color.white);
    private Font _font;
    private bool _lit;

    private GUIStyle _titleBar;
    private GUIStyle _clock;
    private GUIStyle _navLabel;
    private GUIStyle _hint;
    private GUIStyle _pageCount;
    private GUIStyle _text;
    private GUIStyle _textLit;
    private GUIStyle _small;
    private GUIStyle _smallLit;
    private GUIStyle _number;
    private GUIStyle _numberLit;
    private GUIStyle _right;
    private GUIStyle _rightLit;
    private GUIStyle _smallButton;
    private GUIStyle _smallButtonLit;
    private GUIStyle _key;
    private GUIStyle _keyLit;
    private GUIStyle _keyOff;
    private GUIStyle _stripLabel;
    private GUIStyle _stripValue;
    private GUIStyle _carLabel;
    private GUIStyle _carName;
    private GUIStyle _carSpec;

    public GUIStyle Heading;
    public GUIStyle Prompt;

    public GUIStyle Text => _lit ? _textLit : _text;
    public GUIStyle Small => _lit ? _smallLit : _small;

    public void Prepare()
    {
        if (_text != null && _font == Avatars.TagFont)
            return;
        _font = Avatars.TagFont;
        _titleBar = TextStyle(25, White, TextAnchor.MiddleLeft);
        _clock = TextStyle(23, White, TextAnchor.MiddleRight);
        _navLabel = TextStyle(17, Muted, TextAnchor.MiddleRight);
        _hint = TextStyle(16, Muted, TextAnchor.MiddleLeft);
        _pageCount = TextStyle(17, Ink, TextAnchor.MiddleRight);
        _text = TextStyle(21, Ink, TextAnchor.MiddleLeft);
        _textLit = TextStyle(21, White, TextAnchor.MiddleLeft);
        _small = TextStyle(15, Muted, TextAnchor.MiddleLeft);
        _smallLit = TextStyle(15, Ink, TextAnchor.MiddleLeft);
        _number = TextStyle(32, Ink, TextAnchor.MiddleCenter);
        _numberLit = TextStyle(32, White, TextAnchor.MiddleCenter);
        _right = TextStyle(19, Ink, TextAnchor.MiddleRight);
        _rightLit = TextStyle(19, White, TextAnchor.MiddleRight);
        _smallButton = TextStyle(15, Ink, TextAnchor.MiddleCenter);
        _smallButtonLit = TextStyle(15, White, TextAnchor.MiddleCenter);
        _key = TextStyle(23, Ink, TextAnchor.MiddleCenter);
        _keyLit = TextStyle(23, White, TextAnchor.MiddleCenter);
        _keyOff = TextStyle(23, Dim, TextAnchor.MiddleCenter);
        _stripLabel = TextStyle(13, Muted, TextAnchor.MiddleLeft);
        _stripValue = TextStyle(24, Ink, TextAnchor.MiddleLeft);
        _carLabel = TextStyle(13, Dim, TextAnchor.MiddleLeft);
        _carName = TextStyle(23, White, TextAnchor.MiddleLeft);
        _carSpec = TextStyle(15, Muted, TextAnchor.MiddleRight);
        Heading = TextStyle(24, Ink, TextAnchor.MiddleLeft);
        Prompt = TextStyle(20, White, TextAnchor.MiddleCenter);
        _splashText = TextStyle(16, new Color(1f, 1f, 0f), TextAnchor.MiddleCenter);
        _splashShadow = TextStyle(16, new Color(0.25f, 0.25f, 0f), TextAnchor.MiddleCenter);
        _splashText.clipping = TextClipping.Overflow;
        _splashShadow.clipping = TextClipping.Overflow;
    }

    public void DrawFrame(string title, string section, string hint, int page, int pages)
    {
        GUI.Box(Panel, "", _panel);
        if (_logo != null)
            GUI.DrawTexture(new Rect(Panel.x + 20, Panel.y + 12, 260, 52), _logo);
        DrawSplash();
        GUI.Label(new Rect(Panel.xMax - 420, Panel.y + 12, 400, 52), section, _navLabel);

        var bar = new Rect(Panel.x + 14, Panel.y + 72, Panel.width - 28, 42);
        Fill(bar, Band);
        Fill(new Rect(bar.x, bar.yMax - 1, bar.width, 1), Accent);
        GUI.Label(new Rect(bar.x + 10, bar.y, bar.width - 130, bar.height), title, _titleBar);
        GUI.Label(new Rect(bar.xMax - 110, bar.y, 100, bar.height), DateTime.Now.ToString("HH:mm"), _clock);

        GUI.Label(new Rect(List.x, Panel.y + 550, List.width - 70, 26), hint, _hint);
        if (pages > 1)
            GUI.Label(new Rect(List.xMax - 70, Panel.y + 550, 70, 26), $"{page}/{pages}", _pageCount);
    }

    // strip under every page
    public void DrawStatus(int players, int maxPlayers, int ping, string car, string spec)
    {
        var counts = new Rect(Status.x, Status.y, CountWidth * 2, Status.height);
        Fill(counts, Band);
        Outline(counts);
        Fill(new Rect(counts.center.x, counts.y, 1, counts.height), Rule);
        GUI.Label(new Rect(counts.x + 12, counts.y + 4, CountWidth, 20), "PLAYERS", _stripLabel);
        GUI.Label(new Rect(counts.x + 12, counts.y + 22, CountWidth, 32), $"{players:00}/{maxPlayers}", _stripValue);
        GUI.Label(new Rect(counts.center.x + 12, counts.y + 4, CountWidth, 20), "PING", _stripLabel);
        GUI.Label(new Rect(counts.center.x + 12, counts.y + 22, CountWidth, 32), ping >= 0 ? $"{ping} ms" : "--", _stripValue);

        var box = new Rect(counts.xMax + 6, Status.y, Status.width - counts.width - 6, Status.height);
        Fill(box, Deep);
        GUI.Label(new Rect(box.x + 14, box.y + 4, box.width - 28, 20), "CURRENT CAR", _carLabel);
        GUI.Label(new Rect(box.x + 14, box.y + 22, box.width - 200, 32), car, _carName);
        GUI.Label(new Rect(box.xMax - 184, box.y + 22, 170, 32), spec, _carSpec);
    }

    public bool SoftKey(int slot, string caption, bool primary, bool enabled)
    {
        var width = (Panel.width - 40 - KeyGap * 2) / 3;
        var rect = new Rect(Panel.x + 20 + slot * (width + KeyGap), Panel.y + 578, width, KeyHeight);
        var lit = enabled && (primary || rect.Contains(Event.current.mousePosition));
        GUI.Box(rect, "", lit ? _buttonHover : _button);
        GUI.Label(rect, caption, !enabled ? _keyOff : lit ? _keyLit : _key);
        return enabled && Clicked(rect);
    }

    public bool Button(Rect rect, string caption)
    {
        var hover = rect.Contains(Event.current.mousePosition);
        GUI.Box(rect, "", hover ? _buttonHover : _button);
        GUI.Label(rect, caption, hover ? _smallButtonLit : _smallButton);
        return Clicked(rect);
    }

    // lit row turns Text and Small white till EndRow
    public void Row(Rect rect, bool selected)
    {
        _lit = selected || rect.Contains(Event.current.mousePosition);
        GUI.Box(rect, "", _lit ? _rowHover : _row);
    }

    public void EndRow() => _lit = false;

    public void RowNumber(Rect rect, int number) =>
        GUI.Label(new Rect(rect.x + 6, rect.y, NumberWidth, rect.height), number.ToString(), _lit ? _numberLit : _number);

    public void RowText(Rect rect, string title, string subtitle = null, string right = null, float indent = NumberWidth + RowInset)
    {
        var width = rect.width - indent - (right != null ? RightWidth : RowInset);
        if (subtitle == null)
        {
            GUI.Label(new Rect(rect.x + indent, rect.y, width, rect.height), title, Text);
        }
        else
        {
            GUI.Label(new Rect(rect.x + indent, rect.y + 3, width, 28), title, Text);
            GUI.Label(new Rect(rect.x + indent, rect.y + 28, width, 20), subtitle, Small);
        }
        if (right != null)
            GUI.Label(new Rect(rect.xMax - RightWidth - RowInset, rect.y, RightWidth, rect.height), right, _lit ? _rightLit : _right);
    }

    public void Input(Rect rect, string text)
    {
        GUI.Box(rect, "", _input);
        GUI.Label(new Rect(rect.x + 12, rect.y, rect.width - 24, rect.height), $"{text}_", _text);
    }

    public static bool Clicked(Rect rect)
    {
        var e = Event.current;
        if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition))
            return false;
        e.Use();
        return true;
    }

    public void Fill(Rect rect, Color color)
    {
        var saved = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, _solid);
        GUI.color = saved;
    }

    private void Outline(Rect rect)
    {
        Fill(new Rect(rect.x, rect.y, rect.width, 1), Rule);
        Fill(new Rect(rect.x, rect.yMax - 1, rect.width, 1), Rule);
        Fill(new Rect(rect.x, rect.y, 1, rect.height), Rule);
        Fill(new Rect(rect.xMax - 1, rect.y, 1, rect.height), Rule);
    }

    private static string[] Splashes()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "ui", "splashes.txt");
        if (!File.Exists(path))
            return Array.Empty<string>();
        return Array.FindAll(File.ReadAllLines(path), l => l.Length > 0 && l[0] != '#');
    }

    // new line every time the menu opens
    private void DrawSplash()
    {
        if (_splashes.Length == 0)
            return;
        if (Time.unscaledTime - _lastFrame > 1f)
            _splash = UnityEngine.Random.Range(0, _splashes.Length);
        _lastFrame = Time.unscaledTime;
        // easter egg, click the logo for the next line
        if (Clicked(new Rect(Panel.x + 20, Panel.y + 12, 120, 52)))
            _splash = (_splash + 1) % _splashes.Length;
        var line = _splashes[_splash];

        var area = new Rect(Panel.x + 135, Panel.y + 16, 330, 44);
        // long ones just get smaller
        var size = Mathf.Min(1f, 34f / line.Length) * (1f + 0.05f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3.3f)));
        var matrix = GUI.matrix;
        var center = new Vector3(area.center.x, area.center.y, 0f);
        GUI.matrix = matrix * Matrix4x4.TRS(center, Quaternion.Euler(0f, 0f, -4f), Vector3.one * size)
            * Matrix4x4.Translate(-center);
        GUI.Label(new Rect(area.x + 2, area.y + 2, area.width, area.height), line, _splashShadow);
        GUI.Label(area, line, _splashText);
        GUI.matrix = matrix;
    }

    private GUIStyle TextStyle(int size, Color color, TextAnchor anchor)
    {
        var style = new GUIStyle { font = _font, fontSize = size, alignment = anchor, clipping = TextClipping.Clip };
        style.normal.textColor = color;
        return style;
    }

    private static GUIStyle Box(string file, Color fallback)
    {
        var style = new GUIStyle();
        var art = Art(file);
        if (art != null)
        {
            style.normal.background = art;
            style.border = new RectOffset(Border, Border, Border, Border);
        }
        else
        {
            style.normal.background = Flat(fallback);
        }
        return style;
    }

    private static Texture2D Art(string file)
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "ui", file + ".png");
        if (!File.Exists(path))
            return null;
        var art = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        return ImageConversion.LoadImage(art, File.ReadAllBytes(path)) ? art : null;
    }

    private static Texture2D Flat(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}
