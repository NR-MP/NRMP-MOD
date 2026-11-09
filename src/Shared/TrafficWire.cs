using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib.Utils;

namespace NightRunnersMP.Shared;

public static class TrafficWire
{
    public const byte NoAuthority = 255;
    public const byte FlagCapable = 1;
    public const byte FlagWants = 2;
    public const int SendRateHz = 15;
    public const int MaxPacketBytes = 1100;
    public const int MaxEntryLength = 64;
    public const int MaxCars = 256;
    public const int HeaderSize = 4 + 2 + 1;
}

public struct TrafficWheel
{
    public const int Size = 6 + 8;
    private const float PositionScale = 1000f;

    public Vector3 Position;
    public Quaternion Rotation;

    public void Write(NetDataWriter w)
    {
        w.Put(Pack(Position.X * PositionScale));
        w.Put(Pack(Position.Y * PositionScale));
        w.Put(Pack(Position.Z * PositionScale));
        w.Put(Pack(Rotation.X * 32767f));
        w.Put(Pack(Rotation.Y * 32767f));
        w.Put(Pack(Rotation.Z * 32767f));
        w.Put(Pack(Rotation.W * 32767f));
    }

    public static TrafficWheel Read(NetDataReader r)
    {
        var position = new Vector3(r.GetShort() / PositionScale, r.GetShort() / PositionScale, r.GetShort() / PositionScale);
        var rotation = new Quaternion(r.GetShort() / 32767f, r.GetShort() / 32767f, r.GetShort() / 32767f, r.GetShort() / 32767f);
        return new TrafficWheel
        {
            Position = position,
            Rotation = rotation.LengthSquared() > 0.25f ? Quaternion.Normalize(rotation) : Quaternion.Identity,
        };
    }

    private static short Pack(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
}

public struct TrafficCar
{
    public const int WheelCount = 4;
    public const int Size = 2 + 12 + 8 + 6 + 1;
    public const int WheelsSize = WheelCount * TrafficWheel.Size;

    public const byte HeadlightsBit = 0x01;
    public const byte BrakeBit = 0x02;
    private const int IndicatorShift = 2;
    private const byte IndicatorMask = 0x03;
    public const byte BlinkBit = 0x10;
    public const byte WheelsBit = 0x40;

    public ushort Id;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public byte Flags;
    public TrafficWheel Wheel0;
    public TrafficWheel Wheel1;
    public TrafficWheel Wheel2;
    public TrafficWheel Wheel3;

    public bool Headlights => (Flags & HeadlightsBit) != 0;
    public bool Braking => (Flags & BrakeBit) != 0;
    public byte Indicator => (byte)((Flags >> IndicatorShift) & IndicatorMask);
    public bool Blink => (Flags & BlinkBit) != 0;
    public bool HasWheels => (Flags & WheelsBit) != 0;

    public int WireSize => Size + (HasWheels ? WheelsSize : 0);

    public static byte PackFlags(bool headlights, bool braking, byte indicator, bool blink, bool wheels) =>
        (byte)((headlights ? HeadlightsBit : 0) | (braking ? BrakeBit : 0) | ((indicator & IndicatorMask) << IndicatorShift)
            | (blink ? BlinkBit : 0) | (wheels ? WheelsBit : 0));

    public CarState ToState(uint timeMs, ushort scene) => new()
    {
        TimeMs = timeMs,
        Scene = scene,
        Position = Position,
        Rotation = Rotation,
        Velocity = Velocity,
    };

    public bool IsSane => Sane.Position(Position);

    public void Write(NetDataWriter w)
    {
        w.Put(Id);
        new CarState { Position = Position, Rotation = Rotation, Velocity = Velocity }.WritePose(w);
        w.Put(Flags);
        if (!HasWheels)
            return;
        Wheel0.Write(w);
        Wheel1.Write(w);
        Wheel2.Write(w);
        Wheel3.Write(w);
    }

    public static TrafficCar Read(NetDataReader r)
    {
        var car = new TrafficCar { Id = r.GetUShort() };
        var pose = CarState.ReadPose(r);
        car.Position = pose.Position;
        car.Rotation = pose.Rotation;
        car.Velocity = pose.Velocity;
        car.Flags = r.GetByte();
        if (!car.HasWheels)
            return car;
        car.Wheel0 = TrafficWheel.Read(r);
        car.Wheel1 = TrafficWheel.Read(r);
        car.Wheel2 = TrafficWheel.Read(r);
        car.Wheel3 = TrafficWheel.Read(r);
        return car;
    }
}

public struct TrafficBatchHeader
{
    public uint TimeMs;
    public ushort Scene;
    public byte Count;

    public void Write(NetDataWriter w)
    {
        w.Put(TimeMs);
        w.Put(Scene);
        w.Put(Count);
    }

    public static TrafficBatchHeader Read(NetDataReader r) => new()
    {
        TimeMs = r.GetUInt(),
        Scene = r.GetUShort(),
        Count = r.GetByte(),
    };
}

public static class TrafficTable
{
    public static void WriteSpawns(NetDataWriter w, IReadOnlyCollection<KeyValuePair<ushort, string>> spawns)
    {
        w.Put((ushort)spawns.Count);
        foreach (var spawn in spawns)
        {
            w.Put(spawn.Key);
            w.Put(spawn.Value, TrafficWire.MaxEntryLength);
        }
    }

    public static bool TryReadSpawns(NetDataReader r, List<KeyValuePair<ushort, string>> into)
    {
        into.Clear();
        if (!r.TryGetUShort(out var count) || count > TrafficWire.MaxCars)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (!r.TryGetUShort(out var id) || !r.TryGetString(out var entry))
                return false;
            if (string.IsNullOrEmpty(entry) || entry.Length > TrafficWire.MaxEntryLength)
                return false;
            into.Add(new KeyValuePair<ushort, string>(id, entry));
        }
        return true;
    }

    public static void WriteIds(NetDataWriter w, IReadOnlyCollection<ushort> ids)
    {
        w.Put((ushort)ids.Count);
        foreach (var id in ids)
            w.Put(id);
    }

    public static bool TryReadIds(NetDataReader r, List<ushort> into)
    {
        into.Clear();
        if (!r.TryGetUShort(out var count) || count > TrafficWire.MaxCars)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (!r.TryGetUShort(out var id))
                return false;
            into.Add(id);
        }
        return true;
    }
}
