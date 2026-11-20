using System;
using System.IO;
using System.Linq;
using NightRunnersMP.Shared;
using TMPro;
using UnityEngine;

namespace NightRunnersMP;

// profile pics: small square png on the wire, texture on screen.
// HideAndDontSave or they die on scene change, nothing in the game points at them
internal static class Avatars
{
    private const int Size = Protocol.AvatarSide;
    private const float TagAlpha = 0.6f;
    private const int TagFontSize = 15;
    private const int SmallestFont = 10;
    private const int SmallestPixelFont = 4;
    private static readonly Color TagShadow = new(0f, 0f, 0f, 0.85f);

    // racer card font, once a card has been seen
    private const string GameFont = "Karma";

    public static Font TagFont;

    // racer cards hand over their font on load, but menu and chat can get there first
    public static void FindFont()
    {
        if (TagFont != null)
            return;
        foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (asset.name.IndexOf(GameFont, StringComparison.OrdinalIgnoreCase) >= 0 && asset.sourceFontFile != null)
            {
                TagFont = asset.sourceFontFile;
                return;
            }
        }
    }
    private static GUIStyle _tagStyle;
    private static bool _pixelsFailed;

    // cropped and scaled, null if there's no usable pic
    public static byte[] Read(string file)
    {
        if (!File.Exists(file))
            return null;
        var source = new Texture2D(2, 2);
        if (!ImageConversion.LoadImage(source, File.ReadAllBytes(file)))
        {
            UnityEngine.Object.Destroy(source);
            return null;
        }

        var side = Math.Min(source.width, source.height);
        var left = (source.width - side) / 2f / source.width;
        var bottom = (source.height - side) / 2f / source.height;
        var scaled = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                scaled.SetPixel(x, y, source.GetPixelBilinear(
                    left + (x + 0.5f) / Size * side / source.width,
                    bottom + (y + 0.5f) / Size * side / source.height));
        scaled.Apply();
        var png = ImageConversion.EncodeToPNG(scaled).ToArray();
        UnityEngine.Object.Destroy(source);
        UnityEngine.Object.Destroy(scaled);
        return png.Length <= Protocol.MaxAvatarBytes ? png : null;
    }

    // null if it's not one of our pics. the header gets checked first so nothing huge ever reaches the decoder
    public static Texture2D Decode(byte[] png)
    {
        if (!Png.IsSquare(png, Size))
            return null;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (ImageConversion.LoadImage(texture, png) && texture.width == Size && texture.height == Size)
            return Keep(texture);
        UnityEngine.Object.Destroy(texture);
        return null;
    }

    // placeholder till someone has a pic, same colour per name
    public static Texture2D Initial(string name)
    {
        var hue = name.Sum(c => c) % 360 / 360f;
        var color = Color.HSVToRGB(hue, 0.6f, 0.75f);
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                texture.SetPixel(x, y, color);
        texture.Apply();
        return Keep(texture);
    }

    public static void Draw(Rect rect, Texture2D picture, bool hasPicture, string name)
    {
        if (picture != null)
            GUI.DrawTexture(rect, picture);
        if (!hasPicture && name.Length > 0)
            GUI.Label(new Rect(rect.center.x - 5, rect.center.y - 11, rect.width, 24), name.Substring(0, 1).ToUpperInvariant());
    }

    // pixel > 1 draws the tag that many times smaller and blows it back up unfiltered, so it breaks into blocks with distance
    public static void DrawTag(Vector2 screen, Remote remote, float fade, float scale, int pixel)
    {
        if (fade <= 0f)
            return;
        if (_tagStyle == null || _tagStyle.font != TagFont)
            _tagStyle = new GUIStyle(GUI.skin.label) { font = TagFont, richText = false, wordWrap = false, clipping = TextClipping.Clip };
        if (pixel <= 1 || _pixelsFailed)
        {
            DrawTag(screen, remote, fade, scale, Screen.width, Screen.height, SmallestFont);
            return;
        }
        var matrix = GUI.matrix;
        var glyphs = _tagStyle.font != null && _tagStyle.font.material != null ? _tagStyle.font.material.mainTexture : null;
        var glyphFilter = glyphs != null ? glyphs.filterMode : default;
        var pictureFilter = remote.Avatar != null ? remote.Avatar.filterMode : default;
        try
        {
            if (glyphs != null)
                glyphs.filterMode = FilterMode.Point;
            if (remote.Avatar != null)
                remote.Avatar.filterMode = FilterMode.Point;
            GUI.matrix = matrix * Matrix4x4.Scale(new Vector3(pixel, pixel, 1f));
            DrawTag(screen / pixel, remote, fade, scale / pixel, Screen.width / (float)pixel, Screen.height / (float)pixel, SmallestPixelFont);
        }
        catch (Exception e)
        {
            _pixelsFailed = true;
            NetBehaviour.Log.LogWarning($"Name tags stay sharp, pixelating them failed: {e.Message}");
        }
        finally
        {
            GUI.matrix = matrix;
            if (glyphs != null)
                glyphs.filterMode = glyphFilter;
            if (remote.Avatar != null)
                remote.Avatar.filterMode = pictureFilter;
        }
    }

    private static void DrawTag(Vector2 screen, Remote remote, float fade, float scale, float screenWidth, float screenHeight, int smallestFont)
    {
        _tagStyle.fontSize = Mathf.Max(smallestFont, Mathf.RoundToInt(TagFontSize * scale));

        var picSide = remote.IsBot ? 0f : Mathf.Round(32 * scale);
        var pad = Mathf.Round(4 * scale);
        var text = new GUIContent(remote.Tag);
        var textSize = _tagStyle.CalcSize(text);
        var textWidth = Mathf.Min(textSize.x, 200 * scale);
        var height = Mathf.Max(picSide, textSize.y) + pad * 2;
        var width = pad + (picSide > 0 ? picSide + pad : 0) + textWidth + pad;
        var box = new Rect(Mathf.Round(screen.x - width / 2f), Mathf.Round(screen.y - height), width, height);
        if (box.xMax < 0 || box.x > screenWidth || box.yMax < 0 || box.y > screenHeight)
            return;

        var color = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, TagAlpha * fade);
        GUI.DrawTexture(box, Texture2D.blackTexture);
        GUI.color = new Color(color.r, color.g, color.b, fade);
        if (picSide > 0)
            Draw(new Rect(box.x + pad, box.y + (height - picSide) / 2f, picSide, picSide), remote.Avatar, remote.HasPicture, remote.Name);
        var label = new Rect(box.x + pad + (picSide > 0 ? picSide + pad : 0), box.y + (height - textSize.y) / 2f, textWidth, textSize.y);
        var white = _tagStyle.normal.textColor;
        _tagStyle.normal.textColor = new Color(TagShadow.r, TagShadow.g, TagShadow.b, TagShadow.a * fade);
        GUI.Label(new Rect(label.x + 1, label.y + 1, label.width, label.height), text, _tagStyle);
        _tagStyle.normal.textColor = white;
        GUI.Label(label, text, _tagStyle);
        // same spot, same style, so the crew's letters land exactly on the white ones
        if (remote.CrewColour is Color crew && remote.Crew.Length > 0)
        {
            _tagStyle.normal.textColor = crew;
            GUI.Label(label, $"[{remote.Crew}]", _tagStyle);
            _tagStyle.normal.textColor = white;
        }
        GUI.color = color;
    }

    private static Texture2D Keep(Texture2D texture)
    {
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }
}
