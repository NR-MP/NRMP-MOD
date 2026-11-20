using LiteNetLib.Utils;

namespace NightRunnersMP.Shared;

public static class ServerStatus
{
    private static readonly byte[] Magic = { (byte)'N', (byte)'R', (byte)'M', (byte)'P' };
    private const int AskSize = 8;
    private const int ReplySize = 6;

    public static void WriteAsk(NetDataWriter w)
    {
        w.Put(Magic);
        w.Put(0);
    }

    public static bool IsAsk(byte[] data, int offset, int size) => size == AskSize && HasMagic(data, offset);

    public static void WriteReply(NetDataWriter w, int online)
    {
        w.Put(Magic);
        w.Put((byte)System.Math.Min(online, 255));
        w.Put((byte)Protocol.MaxPlayers);
    }

    public static bool TryReadReply(byte[] data, int offset, int size, out int online, out int max)
    {
        online = max = 0;
        if (size != ReplySize || !HasMagic(data, offset))
            return false;
        online = data[offset + 4];
        max = data[offset + 5];
        return true;
    }

    private static bool HasMagic(byte[] data, int offset)
    {
        if (data == null || data.Length < offset + Magic.Length)
            return false;
        for (var i = 0; i < Magic.Length; i++)
            if (data[offset + i] != Magic[i])
                return false;
        return true;
    }
}
