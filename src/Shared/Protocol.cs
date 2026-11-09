using System;
using System.Numerics;
using LiteNetLib.Utils;

namespace NightRunnersMP.Shared;

public static class Protocol
{
    // bump on any change to existing messages, server rejects mismatches. new types need no bump, both sides skip unknown ones
    public const ushort Version = 6;
    // older mods still get in, they just miss engine sound
    public const ushort OldestVersion = 4;
    public const int DefaultPort = 7777;
    public const int MaxPlayers = 32;
    public const int MaxWager = 100000000;
    public const int MaxRaceCounters = 3;
    public const int MaxRaceJumps = 3;
    public const byte NoRacer = 255;
    public const int MaxNameLength = 24;
    public const int SendRateHz = 30;
    public const int MaxPaintBytes = 256;
    public const int MaxAvatarBytes = 32768;
    public const int AvatarSide = 64;
    public const int MaxChatLength = 200;
    public const int MaxBlobBytes = 4096;
    public const int MaxBlobKinds = 16;
    public const int MaxLiveryBytes = 8 * 1024 * 1024;
    public const int LiveryPartBytes = 16384;
    public const int LiveryHashBytes = 16;
    public const int MinCrewLength = 2;
    public const int MaxCrewLength = 16;
    public const int MaxCrewMembers = 64;
    public const int MaxSteamIdLength = 20;
}

public enum RaceEnd : byte
{
    Jumped,
    Quit,
    Left,
    Stalled,
}

public enum MsgType : byte
{
    Welcome = 1,
    PlayerJoined = 2,
    PlayerLeft = 3,
    CarState = 4,
    CarStateBatch = 5,
    SetCar = 6,
    CarChanged = 7,
    SetPaint = 8,
    CarPainted = 9,
    SetAvatar = 10,
    PlayerAvatar = 11,
    Chat = 12,
    ChatMessage = 13,
    Walk = 14,
    PlayerWalk = 15,
    SetBlob = 16,
    PlayerBlob = 17,
    Identify = 18,
    CrewCommand = 19,
    CrewList = 20,
    CrewNotice = 21,
    CrewChat = 22,
    CrewChatMessage = 23,
    RaceChallenge = 24,
    RaceOffer = 25,
    RaceAnswer = 26,
    RaceStart = 27,
    RaceCancelled = 28,
    RaceReady = 29,
    RaceGo = 30,
    RaceFinished = 31,
    RaceResult = 32,
    RaceCounter = 33,
    RaceStarted = 34,
    LiveryPart = 35,
    LiveryWant = 36,
    RaceJumped = 37,
    RaceRestage = 38,
    RaceEnded = 39,
    TrafficHello = 40,
    TrafficRole = 41,
    TrafficSpawn = 42,
    TrafficBatch = 43,
    TrafficGone = 44,
}

// stuff everyone should see, late joiners too. server stores and forwards blobs unread, new kind = no server change, no bump
public enum BlobKind : byte
{
    Build = 1,
    Stats = 2,
    Map = 3,
    // a car hit for one player, everyone gets it and only they use it
    Hit = 4,
    Hinges = 5,
}

public enum RejectReason : byte
{
    BadRequest = 1,
    VersionMismatch = 2,
    WrongKey = 3,
    ServerFull = 4,
}

public struct ConnectRequest
{
    public ushort ProtocolVersion;
    public string JoinKey;
    public string Name;

    public void Write(NetDataWriter w)
    {
        w.Put(ProtocolVersion);
        w.Put(JoinKey ?? "");
        w.Put(Name ?? "");
    }

    public static bool TryRead(NetDataReader r, out ConnectRequest request)
    {
        request = default;
        if (!r.TryGetUShort(out request.ProtocolVersion)) return false;
        if (!r.TryGetString(out request.JoinKey)) return false;
        return r.TryGetString(out request.Name);
    }
}

// always true world coords, never floating origin ones
public struct CarState
{
    public const float VelocityScale = 100f;
    public const int Size = 4 + 2 + 12 + 8 + 6;
    public const int DriveSize = 11;
    private const byte ReverseBit = 0x80;

    public uint TimeMs;
    public ushort Scene;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public ushort Rpm;
    public byte Gas;
    public byte Gear;
    public sbyte Steer;
    public short FrontWheelRpm;
    public short RearWheelRpm;
    public byte Slip;
    public byte Smoke;

    public const byte SmokeFront = 1;
    public const byte SmokeRear = 2;
    // rides in the smoke byte, older clients only look at the low bits
    public const byte SolidBit = 0x80;

    public bool Solid => (Smoke & SolidBit) != 0;

    public const byte LowBeamBit = 0x04;
    public const byte HighBeamBit = 0x08;
    public const byte BrakeBit = 0x10;
    private const int IndicatorShift = 5;
    private const byte IndicatorMask = 0x03;

    public bool LowBeam => (Smoke & LowBeamBit) != 0;
    public bool HighBeam => (Smoke & HighBeamBit) != 0;
    public bool Braking => (Smoke & BrakeBit) != 0;
    public int Indicators => (Smoke >> IndicatorShift) & IndicatorMask;

    public static byte PackLights(bool lowBeam, bool highBeam, bool braking, int indicators) =>
        (byte)((lowBeam ? LowBeamBit : 0) | (highBeam ? HighBeamBit : 0) | (braking ? BrakeBit : 0) | ((indicators & IndicatorMask) << IndicatorShift));

    public float FrontSlip => (Slip & 0x0F) / 15f;
    public float RearSlip => (Slip >> 4) / 15f;

    public static byte PackSlip(float front, float rear) => (byte)((int)(Math.Clamp(front, 0f, 1f) * 15f) | ((int)(Math.Clamp(rear, 0f, 1f) * 15f) << 4));

    public int GearNumber => Gear & ~ReverseBit;
    public bool Reverse => (Gear & ReverseBit) != 0;

    public static byte PackGear(int gear, bool reverse) => (byte)((gear & ~ReverseBit) | (reverse ? ReverseBit : 0));

    // kept off the end so a version 4 client still gets the packets it knows
    public void WriteDrive(NetDataWriter w)
    {
        w.Put(Rpm);
        w.Put(Gas);
        w.Put(Gear);
        w.Put(Steer);
        w.Put(FrontWheelRpm);
        w.Put(RearWheelRpm);
        w.Put(Slip);
        w.Put(Smoke);
    }

    public void ReadDrive(NetDataReader r)
    {
        Rpm = r.GetUShort();
        Gas = r.GetByte();
        Gear = r.GetByte();
        Steer = r.GetSByte();
        FrontWheelRpm = r.GetShort();
        RearWheelRpm = r.GetShort();
        Slip = r.GetByte();
        Smoke = r.GetByte();
    }

    public void Write(NetDataWriter w)
    {
        w.Put(TimeMs);
        w.Put(Scene);
        WritePose(w);
    }

    public void WritePose(NetDataWriter w)
    {
        w.Put(Position.X);
        w.Put(Position.Y);
        w.Put(Position.Z);
        w.Put(PackUnit(Rotation.X));
        w.Put(PackUnit(Rotation.Y));
        w.Put(PackUnit(Rotation.Z));
        w.Put(PackUnit(Rotation.W));
        w.Put(PackVelocity(Velocity.X));
        w.Put(PackVelocity(Velocity.Y));
        w.Put(PackVelocity(Velocity.Z));
    }

    public static CarState Read(NetDataReader r)
    {
        var timeMs = r.GetUInt();
        var scene = r.GetUShort();
        var s = ReadPose(r);
        s.TimeMs = timeMs;
        s.Scene = scene;
        return s;
    }

    public static CarState ReadPose(NetDataReader r)
    {
        var s = new CarState
        {
            Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        };
        var rotation = new Quaternion(r.GetShort() / 32767f, r.GetShort() / 32767f, r.GetShort() / 32767f, r.GetShort() / 32767f);
        // all zeros would normalize to NaN and spin the car into nowhere
        s.Rotation = rotation.LengthSquared() > 0.25f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        s.Velocity = new Vector3(
            r.GetShort() / VelocityScale, r.GetShort() / VelocityScale, r.GetShort() / VelocityScale);
        return s;
    }

    // a real spot in the world, anything else is a broken or hostile sender
    public bool IsSane => Sane.Position(Position);

    private static short PackUnit(float v) => (short)(Math.Clamp(v, -1f, 1f) * 32767f);

    private static short PackVelocity(float v) =>
        (short)Math.Clamp(v * VelocityScale, short.MinValue, short.MaxValue);
}

// on foot at a stop. pos and yaw relative to the first space, a stop's place in the shifted world differs per client. stops sending = gone
public struct WalkState
{
    public const int Size = 2 + 12 + 4;

    public ushort Scene;
    public Vector3 Position;
    public float Yaw;

    public void Write(NetDataWriter w)
    {
        w.Put(Scene);
        w.Put(Position.X);
        w.Put(Position.Y);
        w.Put(Position.Z);
        w.Put(Yaw);
    }

    public bool IsSane => Sane.Position(Position) && float.IsFinite(Yaw);

    public static WalkState Read(NetDataReader r) => new()
    {
        Scene = r.GetUShort(),
        Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        Yaw = r.GetFloat(),
    };
}

public static class Sane
{
    // the map is a few tens of km, true world coords never get near this
    public const float MaxCoordinate = 1e7f;

    public static bool Position(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && Math.Abs(p.X) < MaxCoordinate && Math.Abs(p.Y) < MaxCoordinate && Math.Abs(p.Z) < MaxCoordinate;
}

public static class SceneId
{
    // FNV-1a, string.GetHashCode differs per process
    public static ushort FromName(string name)
    {
        uint h = 2166136261;
        foreach (var c in name)
        {
            h ^= c;
            h *= 16777619;
        }
        return (ushort)(h ^ (h >> 16));
    }
}
