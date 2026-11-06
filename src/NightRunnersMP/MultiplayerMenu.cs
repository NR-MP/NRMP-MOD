using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using NightRunnersMP.Shared;
using PlanetJem.Input.Cursor;
using Rewired;
using UnityEngine;

namespace NightRunnersMP;

// F7, mouse driven. game keys and mouse off while open. have to ask the game's cursor system for a free cursor or it relocks every frame
internal sealed class MultiplayerMenu
{
    private enum Page { Home, Rooms, Teleport, TeleportPlayers, Crew, Players, Cars, Profile, Settings }

    private const KeyCode ToggleKey = KeyCode.F7;
    private const int CursorPriority = 1000;
    private const float RowHeight = 52f;
    private const float RowGap = 4f;
    private const float ButtonWidth = 100f;
    private const float AvatarIndent = 58f;

    private static readonly Color ScreenDim = new(0f, 0f, 0f, 0.5f);
    // rooms are parked for now, maps sit in teleport
    private static readonly Page[] Destinations = { Page.Teleport, Page.Cars, Page.Crew, Page.Players, Page.Profile, Page.Settings };

    private readonly ManualLogSource _log;
    private readonly CarPicker _picker;
    private readonly CrewState _crews;
    private readonly PlayerRace _race;
    private readonly Teleports _teleports;
    private readonly Maps _maps;
    private readonly Teleports.Locate _locate;
    private readonly Action _applyCollisions;
    private readonly Traffic _traffic;
    private readonly Il2CppSystem.Object _cursorOwner = new();
    private MenuUi _ui;
    private Il2CppSystem.IDisposable _cursor;
    private bool _open;
    private Page _page;
    private int _selected;
    private int _listPage;
    private int _pageCount = 1;
    private string _primary;
    private Action _primaryAction;
    private RCC_CarControllerV3 _carShown;
    private string _carName = "ON FOOT";
    private string _carSpec = "";
    private List<Teleports.Place> _places = new();
    private bool _cursorWasVisible;
    private CursorLockMode _cursorWasLocked;
    private string _crewDraft = "";
    private bool _storage;
    private int _sure = -1;

    public bool IsOpen => _open;

    public bool AtDealer { get; set; }

    public int Ping { get; set; } = -1;

    public MultiplayerMenu(ManualLogSource log, CarPicker picker, CrewState crews, PlayerRace race, Teleports teleports, Maps maps, Teleports.Locate locate, Action applyCollisions, Traffic traffic)
    {
        _log = log;
        _picker = picker;
        _crews = crews;
        _race = race;
        _teleports = teleports;
        _maps = maps;
        _locate = locate;
        _applyCollisions = applyCollisions;
        _traffic = traffic;
    }

    public void Draw(bool connected, bool blocked, string myName, Texture2D myPicture, bool hasPicture, ICollection<Remote> remotes, RCC_CarControllerV3 car)
    {
        if (!connected)
        {
            if (_open)
                SetOpen(false);
            return;
        }
        var e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == ToggleKey && !blocked)
        {
            if (!_open)
                Open(AtDealer ? Page.Cars : Page.Home);
            SetOpen(!_open);
            e.Use();
            return;
        }
        _ui ??= new MenuUi();
        _ui.Prepare();
        if (!_open)
        {
            if (AtDealer && !blocked)
                GUI.Label(new Rect(0, Screen.height - 120, Screen.width, 32), $"CAR DEALER: PRESS {ToggleKey}", _ui.Prompt);
            return;
        }

        // in case the cursor system isn't up yet
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        _ui.Fill(new Rect(0, 0, Screen.width, Screen.height), ScreenDim);

        var scale = Mathf.Min(Screen.width / MenuUi.DesignWidth, Screen.height / MenuUi.DesignHeight);
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(
            new Vector3((Screen.width - MenuUi.DesignWidth * scale) / 2, (Screen.height - MenuUi.DesignHeight * scale) / 2, 0),
            Quaternion.identity, new Vector3(scale, scale, 1));
        try
        {
            ReadCar(car);
            var players = remotes.Count + 1;
            _ui.DrawFrame(PageTitle, PageSection, PageHint, _listPage + 1, _pageCount);
            _ui.DrawStatus(players, Protocol.MaxPlayers, Ping, _carName, _carSpec);
            _primary = null;
            _primaryAction = null;
            _pageCount = 1;
            switch (_page)
            {
                case Page.Home:
                    DrawHome(myName, players);
                    break;
                case Page.Rooms:
                    DrawRooms(remotes);
                    break;
                case Page.Teleport:
                    DrawTeleport(remotes);
                    break;
                case Page.TeleportPlayers:
                    DrawTeleportPlayers(remotes);
                    break;
                case Page.Crew:
                    DrawCrew(remotes);
                    break;
                case Page.Players:
                    DrawPlayers(myName, myPicture, hasPicture, remotes);
                    break;
                case Page.Cars:
                    DrawCars(car);
                    break;
                case Page.Profile:
                    DrawProfile(myName, myPicture, hasPicture);
                    break;
                case Page.Settings:
                    DrawSettings();
                    break;
            }
            DrawSoftKeys();
        }
        finally
        {
            GUI.matrix = matrix;
        }

        if (e.type == EventType.KeyDown)
        {
            HandleKey(e);
            e.Use();
        }
    }

    private string PageTitle => _page switch
    {
        Page.Rooms => "Select a room",
        Page.Crew => _crews.Mine != null ? _crews.Mine.Name : "Start or join a crew",
        Page.Players => "Players on this server",
        Page.Cars => "Select a car",
        Page.Teleport => "Select a place",
        Page.TeleportPlayers => "Select a player",
        Page.Profile => "Your profile",
        Page.Settings => "Settings",
        _ => "Select a destination",
    };

    private string PageSection => _page == Page.Home ? "MAIN MENU" : _page == Page.Rooms ? "SERVER BROWSER" : _page == Page.TeleportPlayers ? "TELEPORT" : _page.ToString().ToUpperInvariant();

    private string PageHint => _page switch
    {
        Page.Rooms => _maps.Status.Length > 0 ? _maps.Status : "Drop maps into the game's Maps folder. Everyone needs the same files to meet.",
        Page.Teleport or Page.TeleportPlayers when _race.Busy => "Finish your race first.",
        Page.Teleport when _maps.Status.Length > 0 => _maps.Status,
        Page.Cars => "Click a car, then add it to your garage.",
        Page.Crew when _crews.Mine == null => "Type a name to start a crew.",
        Page.Settings => "Only changes your own game.",
        Page.Players => "To race someone, walk up to their car at a PA.",
        Page.Profile => $"{ToggleKey} or Esc closes the menu.",
        _ => "Click a row twice, or select it and press the red key.",
    };

    private void ReadCar(RCC_CarControllerV3 car)
    {
        if (car == _carShown)
            return;
        _carShown = car;
        var origin = car != null && car.carLocal != null ? car.carLocal.carOrigin : null;
        _carName = origin != null ? PlanetJem.Localization.Locale.GetCarModelName(origin, false) : "ON FOOT";
        _carSpec = car != null ? $"{car._wheelTypeChoise}  ·  {car.totalGears}-SPEED" : "";
    }

    private void DrawSoftKeys()
    {
        if (_ui.SoftKey(0, _page == Page.Home ? "Close" : "Back", false, true))
        {
            if (_page == Page.Home)
                SetOpen(false);
            else
                Open(_page == Page.TeleportPlayers ? Page.Teleport : Page.Home);
        }
        if (_ui.SoftKey(1, "Next page", false, _pageCount > 1))
            _listPage = (_listPage + 1) % _pageCount;
        if (_ui.SoftKey(2, _primary ?? "Enter", true, _primaryAction != null))
            _primaryAction();
    }

    // one page of rows, wheel pages. click selects, second click opens
    private void DrawList(Rect area, int count, Action<Rect, int> row, Action<int> open = null, bool canSelect = true)
    {
        var fit = Mathf.Max(1, Mathf.FloorToInt((area.height + RowGap) / (RowHeight + RowGap)));
        _pageCount = Math.Max(1, (count + fit - 1) / fit);
        var e = Event.current;
        if (e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
        {
            _listPage += Math.Sign(e.delta.y);
            e.Use();
        }
        _listPage = Mathf.Clamp(_listPage, 0, _pageCount - 1);
        for (var i = _listPage * fit; i < Math.Min(count, (_listPage + 1) * fit); i++)
        {
            var rect = new Rect(area.x, area.y + (i % fit) * (RowHeight + RowGap), area.width, RowHeight);
            _ui.Row(rect, canSelect && i == _selected);
            row(rect, i);
            _ui.EndRow();
            if (!canSelect || !MenuUi.Clicked(rect))
                continue;
            if (i == _selected)
                open?.Invoke(i);
            else
                _selected = i;
        }
    }

    private static Rect ButtonIn(Rect row, int fromRight) =>
        new(row.xMax - (fromRight + 1) * (ButtonWidth + 6), row.y + 8, ButtonWidth, row.height - 16);

    private void DrawHome(string myName, int players)
    {
        var page = Destinations[Mathf.Clamp(_selected, 0, Destinations.Length - 1)];
        _primary = "Open";
        _primaryAction = () => Open(page);
        DrawList(MenuUi.List, Destinations.Length, (rect, i) =>
        {
            _ui.RowNumber(rect, i + 1);
            var (title, subtitle, right) = Destinations[i] switch
            {
                Page.Rooms => ("ROOMS", _maps.Current?.Name ?? "Server browser", null),
                Page.Teleport => ("TELEPORT", _maps.Current?.Name ?? "Places, maps and players", null),
                Page.Crew => ("CREW", _crews.Mine?.Name ?? "No crew yet", null),
                Page.Players => ("PLAYERS", "Everyone on this server", $"{players:00}/{Protocol.MaxPlayers}"),
                Page.Settings => ("SETTINGS", "Your game", null),
                Page.Cars => ("CARS", "Spawn any car into your garage", null),
                _ => ("PROFILE", myName, (string)null),
            };
            _ui.RowText(rect, title, subtitle, right);
        }, i => Open(Destinations[i]));
    }

    // haruna first, then my maps, then maps others are on that i can't join
    private void DrawRooms(ICollection<Remote> remotes)
    {
        var here = _maps.Current?.Hash ?? "";
        var rooms = new List<(string Title, string Subtitle, string Hash, CustomMap Map)>
        {
            ("NIGHT-RUNNERS MULTIPLAYER", "Mount Haruna  ·  PA hub  ·  Free drive", "", null),
        };
        rooms.AddRange(_maps.Installed.Select(m => (m.Name.ToUpperInvariant(), m.Author.Length > 0 ? $"Custom map by {m.Author}" : "Custom map", m.Hash, m)));
        foreach (var group in remotes.Where(r => rooms.All(room => room.Hash != r.MapHash)).GroupBy(r => r.MapHash))
        {
            var first = group.First();
            var otherVersion = _maps.Installed.Any(m => m.Id == first.MapId);
            rooms.Add((first.MapName.ToUpperInvariant(), otherVersion ? "OTHER VERSION, you need the same files" : "NOT INSTALLED", group.Key, null));
        }

        if (_selected < rooms.Count && !_race.Busy && !_maps.Busy && !_teleports.Busy && rooms[_selected].Hash != here)
        {
            var room = rooms[_selected];
            if (room.Map != null)
                (_primary, _primaryAction) = ("Travel", () => EnterMap(room.Map));
            else if (room.Hash.Length == 0)
                (_primary, _primaryAction) = ("Travel", BackToHaruna);
        }
        DrawList(MenuUi.List, rooms.Count, (rect, i) =>
        {
            var room = rooms[i];
            var count = remotes.Count(r => r.MapHash == room.Hash) + (here == room.Hash ? 1 : 0);
            _ui.RowNumber(rect, i + 1);
            _ui.RowText(rect, room.Title, here == room.Hash ? $"{room.Subtitle}  ·  YOU ARE HERE" : room.Subtitle, $"{count:00}/{Protocol.MaxPlayers}");
        });
    }

    private void EnterMap(CustomMap map)
    {
        _maps.Enter(map);
        SetOpen(false);
    }

    private void BackToHaruna()
    {
        var pa = _teleports.FirstPa();
        if (pa == null)
            return;
        _maps.Leave();
        _teleports.Go(pa);
        SetOpen(false);
    }

    private List<Teleports.Place> PlayersHere(ICollection<Remote> remotes)
    {
        var map = _maps.Current?.Hash ?? "";
        var road = SceneId.FromName(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        return remotes.Where(r => !r.IsBot && r.Interp.HasData && r.MapHash == map)
            .Select(r => _teleports.ToPlayer(r, _locate, map.Length == 0 && r.Interp.Scene != road, map.Length > 0))
            .ToList();
    }

    // players get their own page, 32 of them would push every place off this one
    private void DrawTeleport(ICollection<Remote> remotes)
    {
        var rows = new List<Teleports.Place> { new() { Name = "Players", Kind = $"{PlayersHere(remotes).Count:00} HERE" } };
        // way out for when the game won't give you the get in prompt
        var god = GodConstant.Instance;
        if (god != null && god.scene_currentType == GodConstant.Scene_currentType.MEETSPOT)
            rows.Add(new() { Name = "Leave PA", Kind = "EXIT", Run = () => _teleports.LeavePa() });
        rows.AddRange(_places);
        DrawPlaces(rows, 1);
    }

    private void DrawTeleportPlayers(ICollection<Remote> remotes) => DrawPlaces(PlayersHere(remotes), 0);

    // the first opensPages rows open a page instead of going anywhere
    private void DrawPlaces(List<Teleports.Place> rows, int opensPages)
    {
        var free = !_race.Busy && !_teleports.Busy && !_maps.Busy;
        void Pick(int i)
        {
            if (i < opensPages)
                Open(Page.TeleportPlayers);
            else if (free)
                Travel(rows[i]);
        }
        if (_selected < rows.Count && (free || _selected < opensPages))
        {
            var picked = _selected;
            _primary = picked < opensPages ? "Open" : "Teleport";
            _primaryAction = () => Pick(picked);
        }
        DrawList(MenuUi.List, rows.Count, (rect, i) =>
        {
            _ui.RowNumber(rect, i + 1);
            _ui.RowText(rect, rows[i].Name, rows[i].Kind);
        }, Pick);
    }

    // anything not on this map is back on the mountain
    private void Travel(Teleports.Place place)
    {
        SetOpen(false);
        if (place.Run != null)
        {
            place.Run();
            return;
        }
        if (!place.Direct)
            _maps.Leave();
        _teleports.Go(place);
    }

    private void DrawSettings()
    {
        DrawList(MenuUi.List, 2, (rect, i) =>
        {
            if (i == 1)
            {
                _ui.RowText(rect, "TRAFFIC", "Turns traffic off on your screen only, everyone else keeps theirs", null, 14f);
                if (_ui.Button(ButtonIn(rect, 0), Traffic.Off.Value ? "OFF" : "ON"))
                    _traffic.Toggle();
                return;
            }
            var on = NetBehaviour.PlayerCollisions.Value;
            _ui.RowText(rect, "PLAYER COLLISIONS", "Other players' cars are solid on your screen", null, 14f);
            if (_ui.Button(ButtonIn(rect, 0), on ? "ON" : "OFF"))
            {
                NetBehaviour.PlayerCollisions.Value = !on;
                _applyCollisions();
            }
        }, canSelect: false);
    }

    private void DrawProfile(string myName, Texture2D picture, bool hasPicture)
    {
        var area = MenuUi.List;
        Avatars.Draw(new Rect(area.x, area.y, 128, 128), picture, hasPicture, myName);
        GUI.Label(new Rect(area.x + 148, area.y + 26, area.width - 148, 40), PlayerStats.OwnTag(myName), _ui.Heading);
        var crew = PlayerStats.OwnCrew.Length > 0 ? PlayerStats.OwnCrew : "NO CREW";
        GUI.Label(new Rect(area.x + 148, area.y + 68, area.width - 148, 30), crew, _ui.Text);
        var god = GodConstant.Instance;
        if (god == null)
            return;
        Stat(0, "MONEY", $"${god.player_Money:N0}");
        Stat(1, "REP", god.player_gameRep.ToString("N0"));
        Stat(2, "CREW REP", god.player_RepCrew.ToString("N0"));
        Stat(3, "RECORD", $"{god.player_raceRecordWins} W / {god.player_raceRecordLoses} L");
    }

    private void Stat(int line, string label, string value)
    {
        var width = (MenuUi.List.width - RowGap) / 2;
        var rect = new Rect(MenuUi.List.x + line % 2 * (width + RowGap), MenuUi.List.y + 144 + line / 2 * (RowHeight + RowGap), width, RowHeight);
        _ui.Row(rect, false);
        GUI.Label(new Rect(rect.x + 14, rect.y, 180, rect.height), label, _ui.Small);
        GUI.Label(new Rect(rect.x + 194, rect.y, rect.width - 208, rect.height), value, _ui.Text);
        _ui.EndRow();
    }

    private void DrawCrew(ICollection<Remote> remotes)
    {
        if (_crews.Mine != null)
            DrawMyCrew(remotes);
        else
            DrawNoCrew();
    }

    private void DrawNoCrew()
    {
        var area = MenuUi.List;
        _ui.Input(new Rect(area.x, area.y, area.width, 48), _crewDraft);
        _primary = "Create crew";
        if (_crewDraft.Trim().Length > 0)
            _primaryAction = CreateCrew;

        var invites = _crews.Invites;
        var others = _crews.All.Where(c => !invites.Contains(c)).ToList();
        GUI.Label(new Rect(area.x, area.y + 52, area.width, 26), invites.Count > 0 ? "INVITES AND CREWS" : "CREWS", _ui.Small);
        DrawList(new Rect(area.x, area.y + 80, area.width, area.height - 80), invites.Count + others.Count, (rect, i) =>
        {
            var crew = i < invites.Count ? invites[i] : others[i - invites.Count];
            _ui.RowText(rect, crew.Name, $"{crew.Members.Count} members", null, 14f);
            if (i >= invites.Count)
                return;
            if (_ui.Button(ButtonIn(rect, 0), "DECLINE"))
                _crews.Decline(crew.Name);
            if (_ui.Button(ButtonIn(rect, 1), "ACCEPT"))
                _crews.Accept(crew.Name);
        }, canSelect: false);
    }

    private void DrawMyCrew(ICollection<Remote> remotes)
    {
        var area = MenuUi.List;
        var crew = _crews.Mine;
        _primary = "Leave crew";
        _primaryAction = _crews.Leave;
        GUI.Label(new Rect(area.x, area.y, area.width, 26), $"LED BY {crew.Members[0].Name}", _ui.Small);

        // leader sees who to invite after the members
        var loose = _crews.IsLeader ? remotes.Where(r => !r.IsBot && r.Crew.Length == 0).ToList() : new List<Remote>();
        DrawList(new Rect(area.x, area.y + 30, area.width, area.height - 30), crew.Members.Count + loose.Count, (rect, i) =>
        {
            if (i >= crew.Members.Count)
            {
                var player = loose[i - crew.Members.Count];
                _ui.RowText(rect, player.Name, "Not in a crew", null, 14f);
                if (_ui.Button(ButtonIn(rect, 0), "INVITE"))
                    _crews.Invite(player.Id);
                return;
            }
            var member = crew.Members[i];
            _ui.RowText(rect, member.Name, member.Slot >= 0 ? "ONLINE" : "OFFLINE", null, 14f);
            if (_crews.IsLeader && member.SteamId != _crews.Id && _ui.Button(ButtonIn(rect, 0), "KICK"))
                _crews.Kick(member.SteamId);
        }, canSelect: false);
    }

    private void CreateCrew()
    {
        if (_crewDraft.Trim().Length == 0)
            return;
        _crews.Create(_crewDraft);
        _crewDraft = "";
    }

    private void DrawPlayers(string myName, Texture2D picture, bool hasPicture, ICollection<Remote> remotes)
    {
        var rows = new List<(string Name, Texture2D Picture, bool Has)> { ($"{PlayerStats.OwnTag(myName)} (YOU)", picture, hasPicture) };
        rows.AddRange(remotes.Select(r => (r.Tag, r.Avatar, r.HasPicture)));
        DrawList(MenuUi.List, rows.Count, (rect, i) =>
        {
            var (name, rowPicture, has) = rows[i];
            Avatars.Draw(new Rect(rect.x + 8, rect.y + 6, 40, 40), rowPicture, has, name);
            _ui.RowText(rect, name, indent: AvatarIndent);
        }, canSelect: false);
    }

    private void DrawCars(RCC_CarControllerV3 car)
    {
        var area = MenuUi.List;
        var rows = _picker.Rows;
        GUI.Label(new Rect(area.x, area.y, area.width - 2 * ButtonWidth - 18, 26), _picker.Status, _ui.Small);
        if (_ui.Button(new Rect(area.xMax - 2 * ButtonWidth - 6, area.y - 2, ButtonWidth, 28), _storage ? "ADD CARS" : "STORAGE"))
        {
            _storage = !_storage;
            _sure = -1;
            _selected = 0;
            _listPage = 0;
        }
        if (_storage)
        {
            DrawStorage(area);
            return;
        }
        if (_ui.Button(new Rect(area.xMax - ButtonWidth, area.y - 2, ButtonWidth, 28), _picker.Auto ? "AUTO" : "MANUAL"))
            _picker.Auto = !_picker.Auto;
        if (_selected < rows.Count)
        {
            var row = rows[_selected];
            var auto = row.HasAuto && (_picker.Auto || !row.HasManual);
            _primary = $"Add {(auto ? "auto" : "manual")}";
            _primaryAction = () => _picker.Pick(rows[_selected], car);
        }
        DrawList(new Rect(area.x, area.y + 30, area.width, area.height - 30), rows.Count, (rect, i) =>
        {
            _ui.RowNumber(rect, i + 1);
            _ui.RowText(rect, rows[i].Name, rows[i].Details, _picker.IsOwned(rows[i].Model) ? "OWNED" : null);
        }, i => _picker.Pick(rows[i], car));
    }

    // cars in the storage lot, DELETE then SURE? so one click can't lose a car
    private void DrawStorage(Rect area)
    {
        var stored = _picker.InStorage();
        DrawList(new Rect(area.x, area.y + 30, area.width, area.height - 30), stored.Count, (rect, i) =>
        {
            _ui.RowNumber(rect, i + 1);
            _ui.RowText(rect, stored[i].Name, "In car storage");
            if (!_ui.Button(ButtonIn(rect, 0), _sure == stored[i].SaveId ? "SURE?" : "DELETE"))
                return;
            if (_sure == stored[i].SaveId)
            {
                _picker.Delete(stored[i]);
                _sure = -1;
            }
            else
            {
                _sure = stored[i].SaveId;
            }
        }, canSelect: false);
    }

    // from the dealer or the main menu
    public void OpenCars()
    {
        Open(Page.Cars);
        if (!_open)
            SetOpen(true);
    }

    private void Open(Page page)
    {
        _page = page;
        if (page == Page.Rooms)
            _maps.Scan();
        if (page == Page.Teleport)
        {
            _maps.Scan();
            _places = _maps.Places();
            if (!_maps.OnPort)
                _places.AddRange(_teleports.Places());
        }
        _selected = 0;
        _listPage = 0;
        _pageCount = 1;
    }

    private void HandleKey(Event e)
    {
        if (e.keyCode == KeyCode.Escape)
            SetOpen(false);
        else if (_page == Page.Crew && _crews.Mine == null && Type(e, ref _crewDraft, Protocol.MaxCrewLength))
            CreateCrew();
    }

    // true on Enter
    private static bool Type(Event e, ref string text, int maxLength)
    {
        switch (e.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                return true;
            case KeyCode.Backspace:
                if (text.Length > 0)
                    text = text.Substring(0, text.Length - 1);
                break;
            default:
                if (!char.IsControl(e.character) && text.Length < maxLength)
                    text += e.character;
                break;
        }
        return false;
    }

    private void SetOpen(bool open)
    {
        _open = open;
        if (open)
        {
            Avatars.FindFont();
            _cursorWasVisible = Cursor.visible;
            _cursorWasLocked = Cursor.lockState;
            if (GameCursor.IsReady)
                _cursor = GameCursor.Acquire(InputCursorMode.Unlocked, _cursorOwner, CursorPriority);
        }
        else
        {
            _cursor?.Dispose();
            _cursor = null;
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _cursorWasLocked;
        }
        if (!ReInput.isReady)
            return;
        ReInput.controllers.Keyboard.enabled = !open;
        ReInput.controllers.Mouse.enabled = !open;
    }
}
