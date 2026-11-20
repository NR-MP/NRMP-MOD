using System;
using System.IO;
using System.IO.Compression;

namespace NightRunnersMP;

internal static class PngPixels
{
    public static int Levels(int width, int height)
    {
        var levels = 1;
        for (var side = Math.Max(width, height); side > 1; side >>= 1)
            levels++;
        return levels;
    }

    public static int Size(int width, int height, int channels, int levels)
    {
        var size = 0;
        for (var i = 0; i < levels; i++)
            size += Math.Max(1, width >> i) * Math.Max(1, height >> i) * channels;
        return size;
    }

    // plain 8 bit rgb and rgba only, null = unity has to read this one. rows come out bottom up like unity keeps them
    public static byte[] Read(byte[] data, bool mips, out int width, out int height, out int channels)
    {
        width = height = channels = 0;
        try
        {
            if (!LiverySync.IsPicture(data, out _) || data[0] != 0x89 || data.Length < 33)
                return null;
            if (data[24] != 8 || data[26] != 0 || data[27] != 0 || data[28] != 0)
                return null;
            var colors = data[25] == 6 ? 4 : data[25] == 2 ? 3 : 0;
            if (colors == 0)
                return null;
            var wide = BigEndian(data, 16);
            var tall = BigEndian(data, 20);

            var packed = new MemoryStream();
            var at = 8;
            while (at + 12 <= data.Length)
            {
                var length = BigEndian(data, at);
                if (length < 0 || length > data.Length - at - 12)
                    return null;
                if (Is(data, at + 4, "IDAT"))
                    packed.Write(data, at + 8, length);
                else if (Is(data, at + 4, "tRNS"))
                    return null;
                else if (Is(data, at + 4, "IEND"))
                    break;
                at += 12 + length;
            }
            packed.Position = 0;

            var levels = mips ? Levels(wide, tall) : 1;
            var pixels = new byte[Size(wide, tall, colors, levels)];
            var stride = wide * colors;
            var row = new byte[stride + 1];
            using (var zlib = new ZLibStream(packed, CompressionMode.Decompress))
            {
                for (var y = 0; y < tall; y++)
                {
                    for (var got = 0; got < row.Length;)
                    {
                        var read = zlib.Read(row, got, row.Length - got);
                        if (read <= 0)
                            return null;
                        got += read;
                    }
                    var to = (tall - 1 - y) * stride;
                    if (!Unfilter(row, pixels, to, y > 0 ? to + stride : -1, stride, colors))
                        return null;
                }
            }
            Shrink(pixels, wide, tall, colors, levels);
            width = wide;
            height = tall;
            channels = colors;
            return pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Unfilter(byte[] row, byte[] pixels, int to, int up, int stride, int channels)
    {
        switch (row[0])
        {
            case 0:
                Buffer.BlockCopy(row, 1, pixels, to, stride);
                return true;
            case 1:
                for (var x = 0; x < stride; x++)
                    pixels[to + x] = (byte)(row[x + 1] + (x >= channels ? pixels[to + x - channels] : 0));
                return true;
            case 2:
                for (var x = 0; x < stride; x++)
                    pixels[to + x] = (byte)(row[x + 1] + (up >= 0 ? pixels[up + x] : 0));
                return true;
            case 3:
                for (var x = 0; x < stride; x++)
                {
                    var left = x >= channels ? pixels[to + x - channels] : 0;
                    var above = up >= 0 ? pixels[up + x] : 0;
                    pixels[to + x] = (byte)(row[x + 1] + ((left + above) >> 1));
                }
                return true;
            case 4:
                for (var x = 0; x < stride; x++)
                {
                    var left = x >= channels ? pixels[to + x - channels] : 0;
                    var above = up >= 0 ? pixels[up + x] : 0;
                    var corner = up >= 0 && x >= channels ? pixels[up + x - channels] : 0;
                    var guess = left + above - corner;
                    var byLeft = Math.Abs(guess - left);
                    var byAbove = Math.Abs(guess - above);
                    var byCorner = Math.Abs(guess - corner);
                    var pick = byLeft <= byAbove && byLeft <= byCorner ? left : byAbove <= byCorner ? above : corner;
                    pixels[to + x] = (byte)(row[x + 1] + pick);
                }
                return true;
            default:
                return false;
        }
    }

    // the smaller copies unity would otherwise make on the game thread
    private static void Shrink(byte[] pixels, int width, int height, int channels, int levels)
    {
        var from = 0;
        for (var level = 1; level < levels; level++)
        {
            var smallWidth = Math.Max(1, width >> 1);
            var smallHeight = Math.Max(1, height >> 1);
            var to = from + width * height * channels;
            for (var y = 0; y < smallHeight; y++)
            {
                var top = from + Math.Min(2 * y, height - 1) * width * channels;
                var bottom = from + Math.Min(2 * y + 1, height - 1) * width * channels;
                for (var x = 0; x < smallWidth; x++)
                {
                    var left = Math.Min(2 * x, width - 1) * channels;
                    var right = Math.Min(2 * x + 1, width - 1) * channels;
                    for (var c = 0; c < channels; c++)
                        pixels[to++] = (byte)((pixels[top + left + c] + pixels[top + right + c] + pixels[bottom + left + c] + pixels[bottom + right + c] + 2) >> 2);
                }
            }
            from += width * height * channels;
            width = smallWidth;
            height = smallHeight;
        }
    }

    private static bool Is(byte[] data, int at, string type) =>
        data[at] == type[0] && data[at + 1] == type[1] && data[at + 2] == type[2] && data[at + 3] == type[3];

    private static int BigEndian(byte[] data, int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
}
