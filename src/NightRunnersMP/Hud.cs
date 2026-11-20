using UnityEngine;

namespace NightRunnersMP;

internal static class Hud
{
    private const float Top = 40f;
    private const float LineHeight = 30f;
    private const int FontSize = 20;

    private static GUIStyle _style;

    public static void Banner(string text, int line)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (_style == null || _style.font != Avatars.TagFont)
            _style = new GUIStyle(GUI.skin.label) { font = Avatars.TagFont, fontSize = FontSize, alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = false };
        var rect = new Rect(0, Top + line * LineHeight, Screen.width, LineHeight);
        var colour = _style.normal.textColor;
        _style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);
        _style.normal.textColor = colour;
        GUI.Label(rect, text, _style);
    }
}
