using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using NightRunnersMP.Shared;

namespace NightRunnersMP;

// local copy of the server's crews + change requests. server decides everything
internal sealed class CrewState
{
    private static readonly List<CrewInfo> None = new();

    private readonly Action<CrewAction, string, byte> _send;
    private string _steamId = "";

    public List<CrewInfo> All { get; private set; } = None;
    public CrewInfo Mine { get; private set; }
    public List<CrewInfo> Invites { get; } = new();

    public bool IsLeader => Mine != null && Mine.Members[0].SteamId == _steamId;

    public string Id => _steamId;

    public CrewState(Action<CrewAction, string, byte> send)
    {
        _send = send;
    }

    public void Read(NetDataReader reader, string steamId)
    {
        _steamId = steamId;
        All = CrewInfo.ReadList(reader);
        Mine = All.Find(c => c.Members.Exists(m => m.SteamId == steamId));
        Invites.Clear();
        Invites.AddRange(All.FindAll(c => c.Invited.Contains(steamId)));
        PlayerStats.OwnCrew = Mine?.Name ?? "";
    }

    // the crew of whoever's in that player slot right now
    public string CrewOf(byte slot) =>
        SafeText.Clean(All.Find(c => c.Members.Exists(m => m.Slot == slot))?.Name, Protocol.MaxCrewLength);

    public void Create(string name) => _send(CrewAction.Create, name, 0);

    public void Invite(byte slot) => _send(CrewAction.Invite, "", slot);

    public void Accept(string crew) => _send(CrewAction.Accept, crew, 0);

    public void Decline(string crew) => _send(CrewAction.Decline, crew, 0);

    public void Leave() => _send(CrewAction.Leave, "", 0);

    public void Kick(string steamId) => _send(CrewAction.Kick, steamId, 0);
}
