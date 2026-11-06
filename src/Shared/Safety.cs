using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LiteNetLib.Utils;

namespace NightRunnersMP.Shared;

// text somebody else typed, shown on my screen. no control or invisible characters, no line breaks, no direction flips,
// no towers of accents. tags = false also takes out < and >, the game's own text reads those as formatting
public static class SafeText
{
    private const int MaxMarks = 2;

    public static string Clean(string text, int max, bool tags = false)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var clean = new StringBuilder(Math.Min(text.Length, max));
        var marks = 0;
        for (var i = 0; i < text.Length && clean.Length < max; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (clean.Length + 2 > max)
                        break;
                    clean.Append(c).Append(text[++i]);
                    marks = 0;
                }
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (clean.Length > 0 && clean[clean.Length - 1] != ' ')
                    clean.Append(' ');
                marks = 0;
                continue;
            }
            if (!tags && (c == '<' || c == '>'))
                continue;
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                    continue;
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.SpacingCombiningMark:
                    if (++marks > MaxMarks)
                        continue;
                    break;
                default:
                    marks = 0;
                    break;
            }
            clean.Append(c);
        }
        return clean.ToString().Trim();
    }
}

// reads that say no instead of throwing or reading past the end
public static class Wire
{
    public static bool TryText(NetDataReader r, int max, out string text)
    {
        text = "";
        if (!r.TryGetString(out var read) || read == null || read.Length > max)
            return false;
        text = read;
        return true;
    }

    public static bool TryBytes(NetDataReader r, int max, out byte[] bytes)
    {
        bytes = null;
        if (r.AvailableBytes < sizeof(ushort))
            return false;
        var length = r.PeekUShort();
        if (length > max || r.AvailableBytes < sizeof(ushort) + length)
            return false;
        r.GetUShort();
        bytes = new byte[length];
        r.GetBytes(bytes, length);
        return true;
    }

    public static void PutBytes(NetDataWriter w, byte[] bytes)
    {
        w.Put((ushort)bytes.Length);
        w.Put(bytes);
    }
}

// steam has no login for us to check, so each pc makes its own key the first time and the server ties the steam id to it.
// after that only that key can be that steam id, the server never sees the private half and nothing on the wire can be replayed
public static class Identity
{
    public const int NonceBytes = 16;
    public const int MaxKeyBytes = 160;
    public const int MaxSignatureBytes = 80;

    // a real steam64 id, plus the -2 my second test window uses
    private static readonly Regex SteamId = new(@"^7656119\d{10}(-2)?$", RegexOptions.CultureInvariant);

    public static bool IsSteamId(string id) => id != null && id.Length <= Protocol.MaxSteamIdLength && SteamId.IsMatch(id);

    public static byte[] Payload(string steamId, byte[] nonce)
    {
        var text = Encoding.UTF8.GetBytes("nrmp identify " + steamId + " ");
        var payload = new byte[text.Length + nonce.Length];
        text.CopyTo(payload, 0);
        nonce.CopyTo(payload, text.Length);
        return payload;
    }

    public static byte[] Sign(ECDsa key, string steamId, byte[] nonce) => key.SignData(Payload(steamId, nonce), HashAlgorithmName.SHA256);

    public static bool Verify(byte[] publicKey, string steamId, byte[] nonce, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            return read == publicKey.Length && key.KeySize == 256 && key.VerifyData(Payload(steamId, nonce), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

// looks at the header only, so nothing big or strange ever reaches an image decoder
public static class Png
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 };

    public static bool IsSquare(byte[] png, int side)
    {
        if (png == null || png.Length < Signature.Length + 8)
            return false;
        for (var i = 0; i < Signature.Length; i++)
            if (png[i] != Signature[i])
                return false;
        return BigEndian(png, 16) == side && BigEndian(png, 20) == side;
    }

    private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
}
