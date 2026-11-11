#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// A session with nobody in it, for the check tour's look round the screens: no Steam, no net, plain data the tour sets scene by scene. Its presses
/// change only its own state, so the real screens can be walked on one PC. Names are the hard ones on purpose: a long one, tags and escapes, Chinese, Cyrillic and an emoji.
/// </summary>
internal sealed class FakeLobbyActions : ILobbyActions
{
    /// <summary>An ordinary name.</summary>
    public const string NameFriend = "Sam";

    /// <summary>A hard name: the widest there is.</summary>
    public const string NameWide = "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW";

    /// <summary>A hard name: tags and escapes that must show as they are.</summary>
    public const string NameTags = "<color=red>x</color>\\n~@|";

    /// <summary>A hard name: three scripts and an emoji.</summary>
    public const string NameMixed = "玩家一号 Ж 😀";

    private const string Protocol = "fake";
    private static readonly IReadOnlyList<PickerFriend> NoFriends = Array.Empty<PickerFriend>();

    private readonly LobbyMeter _meter = new();
    private LobbyState _state = LobbyState.None;
    private IReadOnlyList<LobbyListing> _listings = Array.Empty<LobbyListing>();
    private IReadOnlyList<PickerFriend> _friends = NoFriends;
    private FailureMessage? _whyNot;

    /// <summary>This PC's id on its own seat: the tour gives it the real one, so its picture is real; other ids are ones Steam knows nobody by.</summary>
    public ulong OwnId { get; set; } = 9000000000;

    /// <summary>The fake's own screens to call, as the session would.</summary>
    public ILobbyScreens? Screens { get; set; }

    /// <summary>The identity its rows compare with.</summary>
    public BrowserIdentity MyIdentity { get; } = new(7, "buildA");

    /// <inheritdoc/>
    public bool InSession => _state.Role != LobbyRole.None;

    /// <inheritdoc/>
    public bool LeavingForTitle => false;

    /// <inheritdoc/>
    public LobbyMeter Meter => _meter;

    /// <inheritdoc/>
    public void NoteOnlineOpened() => _meter.OnlineOpened(Streaming.FrameClock.NowMicros);

    /// <summary>What the next snapshot shows.</summary>
    public void SetState(LobbyState state) => _state = state;

    /// <summary>What the next listing shows: a new list object, as the real one makes when something changes.</summary>
    public void SetListings(params LobbyListing[] listings) => _listings = listings;

    /// <summary>Friends for the invite panel.</summary>
    public void SetFriends(params PickerFriend[] friends) => _friends = friends;

    /// <summary>Why Online can't be used, or null.</summary>
    public void SetWhyNot(FailureMessage? why) => _whyNot = why;

    /// <inheritdoc/>
    public LobbyState Snapshot() => _state;

    /// <inheritdoc/>
    public FailureMessage? WhyNotOnline() => _whyNot;

    /// <inheritdoc/>
    public void HostLobby()
    {
        ModLog.Info("Lobby (fake): Host Lobby.");
        _state = HostAlone(OwnId);
        Screens?.ShowLobby();
    }

    /// <inheritdoc/>
    public void JoinLobby(ulong lobby)
    {
        ModLog.Info($"Lobby (fake): join lobby {ModLog.Scrub.Tags.Lobby(lobby)}.");
        _state = FriendView(OwnId);
        Screens?.ShowLobby();
    }

    /// <inheritdoc/>
    public void CancelJoin() => ModLog.Info("Lobby (fake): join cancelled.");

    /// <inheritdoc/>
    public void LeaveOrClose()
    {
        ModLog.Info("Lobby (fake): leave or close.");
        LobbyRole role = _state.Role;
        _state = LobbyState.None;
        Screens?.HideLobby();
        if (role == LobbyRole.Friend)
        {
            Screens?.ShowJoinList(0);
        }
    }

    /// <inheritdoc/>
    public void SetMode(LobbyMode mode)
    {
        ModLog.Info($"Lobby (fake): mode {mode}.");
        _state = _state with { Mode = mode, Save = mode == LobbyMode.QuickGame ? LobbySaveRow.None : Slots()[0] };
    }

    /// <inheritdoc/>
    public bool SetSlot(int slot)
    {
        ModLog.Info($"Lobby (fake): slot {slot}.");
        IReadOnlyList<LobbySaveRow> slots = Slots();
        if (slot < 1 || slot > slots.Count)
        {
            return false;
        }

        _state = _state with { Save = slots[slot - 1] };
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<LobbySaveRow> Slots() => new[]
    {
        new LobbySaveRow(1, true, "Kraken", 2, 5, DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds()),
        new LobbySaveRow(2, false, string.Empty, 0, 0, 0),
        new LobbySaveRow(3, true, "Golfers", 1, 2, DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()),
    };

    /// <inheritdoc/>
    public void Start(bool newRun, bool anyway)
    {
        ModLog.Info($"Lobby (fake): Start (new run {newRun}, anyway {anyway}).");
        _state = _state.NotHere.Count > 0 && !anyway ? _state with { WaitingForEveryone = true } : _state with { Phase = LobbyPhase.Counting, CountdownMs = 3000, WhyNot = StartBlock.NotOpen };
    }

    /// <inheritdoc/>
    public void CancelStart()
    {
        ModLog.Info("Lobby (fake): Start cancelled.");
        _state = _state with { Phase = LobbyPhase.Open, CountdownMs = 0, WaitingForEveryone = false, WhyNot = StartBlock.None };
    }

    /// <inheritdoc/>
    public void ToggleReady()
    {
        LobbySeat? own = _state.SelfSeat;
        if (own != null)
        {
            var seats = new List<LobbySeat>(_state.Seats);
            seats[own.Index] = own with { Ready = !own.Ready };
            _state = _state with { Seats = seats };
        }
    }

    /// <inheritdoc/>
    public void NoteActivity(HostActivity activity) => _state = _state with { Activity = activity };

    /// <inheritdoc/>
    public bool Invite(ulong friend)
    {
        Screens?.ShowToast(new LobbyLine(LobbyWords.ToastInviteSent, "Sam"));
        return true;
    }

    /// <inheritdoc/>
    public bool OpenInviteOverlay() => true;

    /// <inheritdoc/>
    public bool OpenProfile(ulong steamId) => true;

    /// <inheritdoc/>
    public void Remove(ulong steamId)
    {
        var seats = new List<LobbySeat>(_state.Seats);
        for (int i = 0; i < seats.Count; i++)
        {
            if (seats[i].SteamId == steamId)
            {
                seats[i] = new LobbySeat { Index = i, PlayerColor = i };
            }
        }

        _state = _state with { Seats = seats };
    }

    /// <inheritdoc/>
    public void OpenOwnSettings() => ModLog.Info("Lobby (fake): own settings.");

    /// <inheritdoc/>
    public void SetBrowserOpen(bool open) => ModLog.Info($"Lobby (fake): browser {(open ? "open" : "closed")}.");

    /// <inheritdoc/>
    public IReadOnlyList<LobbyListing> Listings() => _listings;

    /// <inheritdoc/>
    public void RefreshListings() => ModLog.Info("Lobby (fake): refresh.");

    /// <inheritdoc/>
    public IReadOnlyList<PickerFriend> Friends() => _friends;

    // ---- The tour's scenes ----

    /// <summary>Five rows: joinable, in game, full, wrong version, invited.</summary>
    public LobbyListing[] FiveRows()
    {
        var mine = new[] { 1UL };
        return new[]
        {
            new LobbyListing { LobbyId = 1, HostId = 9000000001, HostName = "Alex", FriendsInIt = new[] { 9000000001UL }, HasDetails = true, Players = 2, MaxPlayers = 4, State = ListingState.InLobby, Mode = LobbyMode.Campaign, Save = new LobbySaveRow(1, true, "Kraken", 2, 5, 0), Protocol = MyIdentity.Protocol, Build = MyIdentity.Build, ModVersion = "0.9.0", PingMs = 58 },
            new LobbyListing { LobbyId = 2, HostId = 9000000002, HostName = NameFriend, FriendsInIt = new[] { 9000000002UL }, HasDetails = true, Players = 3, MaxPlayers = 4, State = ListingState.InGame, Mode = LobbyMode.Campaign, Save = new LobbySaveRow(2, true, "Golfers", 3, 11, 0), Protocol = MyIdentity.Protocol, Build = MyIdentity.Build, ModVersion = "0.9.0", PingMs = 120 },
            new LobbyListing { LobbyId = 3, HostId = 9000000003, HostName = NameWide, FriendsInIt = new[] { 9000000003UL }, HasDetails = true, Players = 4, MaxPlayers = 4, State = ListingState.InLobby, Mode = LobbyMode.QuickGame, Protocol = MyIdentity.Protocol, Build = MyIdentity.Build, ModVersion = "0.9.0", PingMs = 90 },
            new LobbyListing { LobbyId = 4, HostId = 9000000004, HostName = DisplayName.Clean(NameTags, 1), FriendsInIt = new[] { 9000000004UL }, HasDetails = true, Players = 1, MaxPlayers = 4, State = ListingState.InLobby, Mode = LobbyMode.Campaign, Protocol = MyIdentity.Protocol - 1, Build = MyIdentity.Build, ModVersion = "0.8.0", PingMs = 250 },
            new LobbyListing { LobbyId = 5, HostId = 9000000005, HostName = DisplayName.Clean(NameMixed, 1, LobbyLocalization.Language(), LobbyScreens.CanDraw), FriendsInIt = new[] { 9000000005UL }, HasDetails = true, Players = 1, MaxPlayers = 4, State = ListingState.InLobby, Mode = LobbyMode.QuickGame, Protocol = MyIdentity.Protocol, Build = MyIdentity.Build, ModVersion = "0.9.0", PingMs = 40, InvitedUs = true },
        };
    }

    /// <summary>Friends for the panel: one in the game, one invited, two others.</summary>
    public static PickerFriend[] SomeFriends() => new[]
    {
        new PickerFriend(9000000011, "Sam", PickerActivity.PlayingT2T, false),
        new PickerFriend(9000000012, NameWide, PickerActivity.OtherGame, false),
        new PickerFriend(9000000013, NameFriend, PickerActivity.Online, true),
        new PickerFriend(9000000014, DisplayName.Clean(NameMixed, 4, LobbyLocalization.Language(), LobbyScreens.CanDraw), PickerActivity.Online, false),
    };

    /// <summary>The host alone: focus lands on an empty seat.</summary>
    public LobbyState HostAlone(ulong self)
    {
        LobbySeat[] seats = EmptySeats(4);
        seats[0] = Person(0, self, "Alex", SeatStage.Here, host: true, self: true);
        return Base(LobbyRole.Host, seats);
    }

    /// <summary>The host with all four seats in every hard state: ready and not, each device, ping 20, 90, 250, one reconnecting.</summary>
    public LobbyState HostFull(ulong self, bool lastReconnecting = true)
    {
        LobbySeat[] seats = EmptySeats(4);
        seats[0] = Person(0, self, "Alex", SeatStage.Here, host: true, self: true);
        seats[1] = Person(1, 9000000002, NameFriend, SeatStage.Here, ready: true, ping: 20, pad: true);
        seats[2] = Person(2, 9000000003, NameWide, SeatStage.Here, ping: 90, pad: false);
        seats[3] = Person(3, 9000000004, lastReconnecting ? NameTags : NameMixed, lastReconnecting ? SeatStage.Reconnecting : SeatStage.Here, ping: 250, seconds: 27, pad: true);
        return Base(LobbyRole.Host, seats);
    }

    /// <summary>Stages not seen above: invited, joining, loading.</summary>
    public LobbyState HostStages(ulong self)
    {
        LobbySeat[] seats = EmptySeats(4);
        seats[0] = Person(0, self, "Alex", SeatStage.Here, host: true, self: true, ready: true);
        seats[1] = Person(1, 9000000002, NameMixed, SeatStage.Joining);
        seats[2] = Person(2, 9000000003, "Sam", SeatStage.Loading, ping: 58);
        seats[3] = Person(3, 9000000004, "Alex", SeatStage.Invited);
        return Base(LobbyRole.Host, seats);
    }

    /// <summary>A friend's view: the host's rows read-only, their own card focused, a status line.</summary>
    public LobbyState FriendView(ulong self)
    {
        LobbySeat[] seats = EmptySeats(4);
        seats[0] = Person(0, 9000000001, "Alex", SeatStage.Here, host: true);
        seats[1] = Person(1, self, NameFriend, SeatStage.Here, self: true, ping: 58);
        seats[2] = Person(2, 9000000003, "Sam", SeatStage.Here, ready: true, ping: 90, pad: false);
        return Base(LobbyRole.Friend, seats) with { Activity = HostActivity.PickingSave, WhyNot = StartBlock.NotHost };
    }

    /// <summary>Up to ten seats, 5 by 2.</summary>
    public LobbyState HostTen(ulong self)
    {
        LobbySeat[] seats = EmptySeats(10);
        seats[0] = Person(0, self, "Alex", SeatStage.Here, host: true, self: true);
        string[] names = { NameFriend, NameWide, NameTags, NameMixed, "Riley", "Casey", "Guest" };
        for (int i = 0; i < names.Length; i++)
        {
            seats[i + 1] = Person(i + 1, 9000000002 + (ulong)i, names[i], SeatStage.Here, ready: i % 2 == 0, ping: 30 + (i * 40), pad: i != 2);
        }

        return Base(LobbyRole.Host, seats);
    }

    /// <summary>The state with a countdown.</summary>
    public static LobbyState Counting(LobbyState state, int ms) => state with { Phase = LobbyPhase.Counting, CountdownMs = ms, WhyNot = StartBlock.NotOpen };

    /// <summary>The state back from a game, with its ribbon.</summary>
    public static LobbyState Back(LobbyState state, LobbyResult result) => state with { Result = result, BackFromGame = true };

    private static LobbySeat[] EmptySeats(int count)
    {
        var seats = new LobbySeat[count];
        for (int i = 0; i < count; i++)
        {
            seats[i] = new LobbySeat { Index = i, PlayerColor = i };
        }

        return seats;
    }

    private static LobbySeat Person(int index, ulong steamId, string name, SeatStage stage, bool host = false, bool self = false, bool ready = false, int ping = -1, int seconds = 0, bool pad = true)
    {
        string clean = DisplayName.Clean(name, index + 1, LobbyLocalization.Language(), LobbyScreens.CanDraw);
        return new LobbySeat
        {
            Index = index,
            SteamId = steamId,
            Name = clean,
            IsHost = host,
            IsSelf = self,
            Stage = stage,
            Ready = ready,
            DeviceKind = pad ? InputKind.Gamepad : InputKind.KeyboardMouse,
            PlayerColor = index,
            PingMs = host ? -1 : ping,
            Bars = host ? 0 : SignalBars.Of(ping),
            ReconnectSeconds = seconds,
        };
    }

    private LobbyState Base(LobbyRole role, LobbySeat[] seats)
    {
        var notHere = new List<LobbySeat>();
        foreach (LobbySeat seat in seats)
        {
            if (seat.Stage is SeatStage.Joining or SeatStage.Loading or SeatStage.Reconnecting)
            {
                notHere.Add(seat);
            }
        }

        return new LobbyState
        {
            Role = role,
            Mode = LobbyMode.Campaign,
            Save = Slots()[0],
            Seats = seats,
            WhyNot = role == LobbyRole.Host ? StartBlock.None : StartBlock.NotHost,
            NotHere = notHere,
        };
    }
}
#endif
