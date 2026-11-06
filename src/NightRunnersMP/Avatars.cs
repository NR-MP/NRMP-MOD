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

    // cropped and scaled, null if there's no usable pic
    public static byte[] Read(string file)
    {
        if (!File.Exists(file))
            return null;
        var source = new Texture2D(2, 2);
        if (!ImageConversion.LoadImage(source, File.ReadAllBytes(file)))
            return null;

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

    public static void DrawTag(Vector2 screen, Remote remote)
    {
        var picture = remote.IsBot ? 0 : 38;
        var width = picture + 122;
        var box = new Rect(screen.x - width / 2f, screen.y - 40, width, 40);
        var color = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, TagAlpha);
        GUI.DrawTexture(box, Texture2D.blackTexture);
        GUI.color = color;
        if (!remote.IsBot)
            Draw(new Rect(box.x + 4, box.y + 4, 32, 32), remote.Avatar, remote.HasPicture, remote.Name);
        if (_tagStyle == null || _tagStyle.font != TagFont)
            _tagStyle = new GUIStyle(GUI.skin.label) { font = TagFont, fontSize = TagFontSize, richText = false };
        GUI.Label(new Rect(box.x + picture + 4, box.y + 8, 116, 24), remote.Tag, _tagStyle);
    }

    private static Texture2D Keep(Texture2D texture)
    {
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }
}
