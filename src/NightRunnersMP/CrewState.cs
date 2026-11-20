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
    private Dictionary<string, int> _colours = new();

    public List<CrewInfo> All { get; private set; } = None;
    public CrewInfo Mine { get; private set; }
    public List<CrewInfo> Invites { get; } = new();

    public bool IsLeader => Mine != null && Mine.Members[0].SteamId == _steamId;

    public string Id => _steamId;

    // only a server that sends colours can store one
    public bool CanColour { get; private set; }

    public int MyColour => Mine != null && _colours.TryGetValue(Mine.Name, out var colour) ? colour : 0;

    public void ReadColours(NetDataReader reader)
    {
        _colours = CrewInfo.ReadColours(reader);
        CanColour = true;
    }

    public UnityEngine.Color? ColourOf(string crew) =>
        crew.Length > 0 && _colours.TryGetValue(crew, out var colour) && colour != 0 ? ToColor(colour) : null;

    public static UnityEngine.Color ToColor(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    public void SetColour(int rgb) => _send(CrewAction.Colour, rgb.ToString("X6"), 0);

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
