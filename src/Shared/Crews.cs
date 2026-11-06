using System.Collections.Generic;
using LiteNetLib.Utils;

namespace NightRunnersMP.Shared;

public enum CrewAction : byte
{
    Create = 1,
    Invite = 2,
    Accept = 3,
    Decline = 4,
    Leave = 5,
    Kick = 6,
}

public sealed class CrewMember
{
    public string SteamId = "";
    public string Name = "";

    // slot while online, else -1. set when the list goes out
    public int Slot = -1;
}

// lives on the server by name, outlasts everyone being offline. first member leads
public sealed class CrewInfo
{
    public string Name = "";
    public List<CrewMember> Members = new();
    public List<string> Invited = new();

    public static void WriteList(NetDataWriter w, List<CrewInfo> crews)
    {
        w.Put((ushort)crews.Count);
        foreach (var crew in crews)
        {
            w.Put(crew.Name);
            w.Put((ushort)crew.Members.Count);
            foreach (var member in crew.Members)
            {
                w.Put(member.SteamId);
                w.Put(member.Name);
                w.Put((sbyte)member.Slot);
            }
            w.Put((ushort)crew.Invited.Count);
            foreach (var invited in crew.Invited)
                w.Put(invited);
        }
    }

    public static List<CrewInfo> ReadList(NetDataReader r)
    {
        var crews = new List<CrewInfo>();
        for (var c = r.GetUShort(); c > 0; c--)
        {
            var crew = new CrewInfo { Name = r.GetString() };
            for (var m = r.GetUShort(); m > 0; m--)
                crew.Members.Add(new CrewMember { SteamId = r.GetString(), Name = r.GetString(), Slot = r.GetSByte() });
            for (var i = r.GetUShort(); i > 0; i--)
                crew.Invited.Add(r.GetString());
            crews.Add(crew);
        }
        return crews;
    }
}
