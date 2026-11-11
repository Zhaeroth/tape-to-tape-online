#if HAS_INTEROP
using System;
using Il2CppBishopGames.Input;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// Steam's side of joining (D20, D49): requests to join that come from Steam itself (an invite accepted, "Join Game" in the
// friends list, a game Steam started for one), the rich presence that makes "Join Game" work, the panel's Invite friends;
// and which pad a friend plays with. Apart from what the session does (OnlineSession.cs), its words
// (OnlineSession.Text.cs) and what the panel reads (OnlineSession.Panel.cs).
internal sealed partial class OnlineSession
{
    /// <summary>The key for the one-PC test while hosting (Ctrl: a stray F6 does nothing).</summary>
    internal const string SoloKey = "Ctrl+F6";

    /// <summary>The key for a simulated drop as a friend (Ctrl: a stray F5, the browser's refresh key, can't hand your skater to the AI).</summary>
    internal const string DropKey = "Ctrl+F5";

    /// <summary>How long the title screen has been up before a game started from an invite joins (the game settles there first).</summary>
    private static readonly TimeSpan TitleSettle = TimeSpan.FromSeconds(2);

    /// <summary>How often a join waiting for the game to start looks at whether it has.</summary>
    private static readonly TimeSpan StartedCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly SteamJoinInbox _steamJoins = new();
    private readonly SteamPresence _presence = new();
    private bool _steamJoinWaiting;
    private TimeSpan _nextStartedCheck;
    private TimeSpan _titleSeenAt = TimeSpan.MinValue;
    private string _padName = string.Empty;
    private string _padLine = string.Empty;
    private (string Name, byte Ours, int Theirs, string Host) _padLineFor;

    /// <summary>
    /// Called before any way of hosting or joining starts (the runtime gives the F8 loopback's pad up first: its virtual pad
    /// would pass for a friend's, Campaign/HostDecides), or null.
    /// </summary>
    public Action? BeforeSessionStarts { get; set; }

    /// <summary>True when the panel's Invite friends can open Steam's invite window: hosting, in the lobby.</summary>
    public bool CanInvite => !_broken && _host != null && _link.Id != 0 && !_link.OutOfLobby;

    /// <summary>
    /// The panel's Invite friends: opens Steam's overlay on its invite window for this session's lobby. Friends who accept
    /// join through the same path as F7 (their game is started for it if it isn't running). Returns whether it opened,
    /// and a line saying so or why not (and what friends can do instead). Never throws.
    /// </summary>
    public (bool Opened, string Message) InviteFriends()
    {
        if (TourRefusesInvite("Steam's invite window was asked for"))
        {
            return (false, "A check tour is running: it never opens Steam's invite window.");
        }

        if (!CanInvite)
        {
            ModLog.Info("Online (invite): not hosting a lobby right now; nothing opened.");
            return (false, "Host a game first: Invite friends then opens Steam's invite window for it.");
        }

        ulong lobby = _link.Id;
        SteamOverlayResult result = SteamOverlay.OpenInviteDialog(lobby);
        switch (result)
        {
            case SteamOverlayResult.Opened:
                ModLog.Info($"Online (invite): Steam's invite window is open for lobby {Tags.Lobby(lobby)}. A friend who accepts joins as Join Lobby does (their game starts for it if it isn't running).");
                return (true, "Steam's invite window is open: pick friends there. One who accepts joins you, starting the game if it isn't running.");
            case SteamOverlayResult.OverlayOff:
                ModLog.Warn("Online (invite): Steam's overlay is off for this game, so its invite window can't open (Steam > Settings > In Game > Enable the Steam Overlay).");
                return (false, "Steam's overlay is off for this game, so its invite window can't open (Steam > Settings > In Game). " +
                    "Friends can still join: Online > Join Lobby, or Join Game on your name in their Steam friends list.");
            default:
                ModLog.Warn($"Online (invite): Steam couldn't open its invite window ({SteamRuntime.Status}).");
                return (false, "Steam couldn't open its invite window (see the log). Friends can still join: Online > Join Lobby, or Join Game on your name in their Steam friends list.");
        }
    }

    /// <summary>
    /// Once Steam is up: hears Steam's join requests from now on, and takes the one the game was started with (an invite
    /// accepted, or "Join Game" used, while the game wasn't running), which waits for the game to finish starting.
    /// </summary>
    private void StartSteamJoins(TimeSpan elapsed)
    {
        if (SteamJoinListener.Install(_steamJoins, () => _clock.Elapsed, out string problem))
        {
            ModLog.Info("Steam: invites and \"Join Game\" are heard: a friend hosting can invite you (the lobby's Invite), or you use Join Game on " +
                "their name in Steam's friends list; either joins as Join Lobby does.");
        }
        else
        {
            ModLog.Warn($"Steam: invites and \"Join Game\" can't be heard while the game runs ({problem}); Join Lobby and a game started from an invite still join.");
        }

        ulong lobby = SteamJoinListener.FromCommandLine();
        if (lobby != 0 && _steamJoins.Offer(new SteamJoinRequest(lobby, 0, SteamJoinSource.Launch, elapsed), out _))
        {
            ModLog.Info($"Steam: this game was started to join lobby {Tags.Lobby(lobby)} (an invite or Join Game); joining once the title screen is up " +
                $"(up to {SteamJoinInbox.LaunchWait.TotalMinutes:0} minutes).");
        }
    }

    /// <summary>Every frame once Steam is up: a request to join from Steam, once the game can act on it.</summary>
    private void PollSteamJoins(TimeSpan elapsed)
    {
        if (TourDropsSteamJoins(elapsed))
        {
            return; // a check tour never joins anyone (OnlineSession.TourHolds.cs)
        }

        if (_steamJoinWaiting && elapsed >= _nextStartedCheck)
        {
            _nextStartedCheck = elapsed + StartedCheckInterval;
            // The title's script up with its warm-ups over, not just its scene in: they take seconds on a slow PC, and only after them does the title choose its button group
            // (InitializeTitle: PrepareTitleMenus, m_HasStarted, GoToPressToStart). A press-start before that is undone, and it runs the title's code on a title not set up.
            if (_titleSeenAt == TimeSpan.MinValue && ReInput.isReady && LobbyShell.TitleAllowingOpen() != null)
            {
                _titleSeenAt = elapsed;
            }
        }

        bool started = _titleSeenAt != TimeSpan.MinValue && elapsed - _titleSeenAt >= TitleSettle;
        SteamJoinPoll poll = _steamJoins.Poll(elapsed, steamReady: false, started, out SteamJoinRequest request);
        _steamJoinWaiting = poll == SteamJoinPoll.Waiting;
        if (poll == SteamJoinPoll.Waiting)
        {
            if (request.Source == SteamJoinSource.Launch && _titleSeenAt != TimeSpan.MinValue && !_heldSaid)
            {
                // Cold start: from the first interactive frame the player is told what's happening, with no press needed.
                _heldSaid = true;
                string host = LobbyHostName(request.LobbyId);
                if (host.Length > 0)
                {
                    NotifyAbout(LobbyHostId(request.LobbyId), NoticeId.JoiningLobby, host);
                }
                else
                {
                    Notify(NoticeId.JoiningPlain, host);
                }

                ModLog.Info($"Steam: this game was started for lobby {Tags.Lobby(request.LobbyId)}; the title is up, joining in a moment.");
            }

            if ((request.Source != SteamJoinSource.Launch || started) && elapsed >= _nextHeldCheck && LobbyQuestion.Current == null)
            {
                _nextHeldCheck = elapsed + StartedCheckInterval;
                SteamJoinVerdict verdict = LobbySignals.JoinVerdict(JoinFacts(request.LobbyId));
                if (verdict == SteamJoinVerdict.Hold)
                {
                    SayHeld(request);
                }
                else if (_steamJoins.Poll(elapsed, steamReady: true, started, out request) == SteamJoinPoll.Ready)
                {
                    _heldSaid = false;
                    Guarded("Steam join", () => JoinFromSteam(request, verdict));
                }
            }
        }
        else if (poll == SteamJoinPoll.Expired)
        {
            _heldSaid = false;
            ModLog.Warn($"Steam: the request to join lobby {Tags.Lobby(request.LobbyId)} ({request.Source}) waited {(elapsed - request.At).TotalSeconds:0} s for the game " +
                "to be ready and is dropped; Join Lobby still joins.");
            Notify(NoticeId.SteamJoinLate);
        }
    }

    /// <summary>What decides a request from Steam now. Asked four times a second while one waits; the title is looked for only then.</summary>
    private SteamJoinFacts JoinFacts(ulong lobby) => new()
    {
        SameLobby = _link.Id == lobby || _link.JoiningLobby == lobby,
        Hosting = _host != null,
        FriendsSeated = _pads.Count + _parked.Count - (_pads.ContainsKey(_self) ? 1 : 0),
        InFriendsLobby = _friend != null || _link.Joining,
        GameUnderWay = _playOn || _flow.Host is not (LobbyHostState.Idle or LobbyHostState.Open),
        AtIdleTitle = !Campaign.RunSync.BlocksJoining(out _) && LobbyShell.TitleAllowingOpen() != null,
    };

    /// <summary>A request held for an idle title: said once, as a toast with who it's from, and logged.</summary>
    private void SayHeld(in SteamJoinRequest request)
    {
        if (_heldSaid)
        {
            return;
        }

        _heldSaid = true;
        string from = request.FromSteamId != 0 ? DisplayNameOf(request.FromSteamId) : LobbyHostName(request.LobbyId);
        ulong fromId = request.FromSteamId != 0 ? request.FromSteamId : LobbyHostId(request.LobbyId);
        bool ownRun = Campaign.RunSync.BlocksJoining(out _);
        ModLog.Info($"Steam: the request to join lobby {Tags.Lobby(request.LobbyId)} ({request.Source}) is held: it joins at an idle title, within {SteamJoinInbox.JoinWait.TotalMinutes:0} minutes.");
        NotifyAbout(fromId, ownRun ? NoticeId.InviteHeldRun : NoticeId.InviteWaits, from);
    }

    /// <summary>The friend who's in lobby <paramref name="lobby"/>, as the friends list says, or empty. Local: no network.</summary>
    private static string LobbyHostName(ulong lobby) => FriendIn(lobby) is { } friend ? ScreenName.Of(friend.PersonaName, 32) : string.Empty;

    /// <summary>The Steam ID of the friend <see cref="LobbyHostName"/> names, or 0 (for the log's copy of a notice: their tag).</summary>
    private static ulong LobbyHostId(ulong lobby) => FriendIn(lobby)?.FriendSteamId ?? 0;

    private static FriendLobby? FriendIn(ulong lobby)
    {
        foreach (FriendLobby friend in SteamFriendsLobbies.FindFriendLobbies(SteamRuntime.AppId))
        {
            if (friend.LobbySteamId == lobby)
            {
                return friend;
            }
        }

        return null;
    }

    /// <summary>
    /// Every frame once online is off after an error: a join Steam asks for meanwhile (the handlers stay in Facepunch's
    /// events) is answered with why nothing happens, not left waiting unseen.
    /// </summary>
    private void RefuseSteamJoins(TimeSpan elapsed)
    {
        if (_steamJoins.Poll(elapsed, steamReady: true, gameStarted: true, out SteamJoinRequest request) == SteamJoinPoll.Ready)
        {
            ModLog.Warn($"Steam: a request to join lobby {Tags.Lobby(request.LobbyId)} ({request.Source}) came while online is off after an error; nothing joined (restart the game to play online).");
            Notify(NoticeId.OnlineBroken);
        }
    }

    /// <summary>
    /// A join Steam asked for (an invite, "Join Game", or the one the game was started with): the path F7 and the panel's
    /// Join take, so everything they check is checked (a session already on, a restored backup waiting for a restart,
    /// online off; once in the lobby, the host's versions, then the host's friends-only seat, D37), and a backup is taken
    /// before following the host's campaign. <paramref name="verdict"/> says what it comes to now (held ones don't get here).
    /// </summary>
    private void JoinFromSteam(SteamJoinRequest request, SteamJoinVerdict verdict)
    {
        ulong lobby = request.LobbyId;
        string from = request.FromSteamId != 0 && request.FromSteamId != _self ? DisplayNameOf(request.FromSteamId) : string.Empty;
        // For the log: who asked, by tag (the screen's words use their name).
        bool named = request.FromSteamId != 0 && request.FromSteamId != _self;
        string how = request.Source switch
        {
            SteamJoinSource.Launch => "the invite this game was started for",
            SteamJoinSource.JoinGame => named ? $"Join Game on {Tag(request.FromSteamId)}" : "Join Game",
            _ => named ? $"an invite from {Tag(request.FromSteamId)} (or Join Game on them)" : "an invite (or Join Game)",
        };
        ModLog.Info($"Steam: {how} asks to join lobby {Tags.Lobby(lobby)}.");
        if (verdict == SteamJoinVerdict.AlreadyThere)
        {
            ModLog.Info($"Online (Steam join): already {(_link.Id == lobby ? "in" : "joining")} lobby {Tags.Lobby(lobby)}; nothing to do.");
            NotifyText(NoticeId.AlreadyInSession, from.Length > 0 ? $"You're already in {from}'s game." : "You're already in that game.",
                named ? $"You're already in {Tag(request.FromSteamId)}'s game." : "You're already in that game.");
            return;
        }

        if (verdict == SteamJoinVerdict.RefuseHosting)
        {
            ModLog.Warn($"Online (Steam join): hosting friends in lobby {Tags.Lobby(_link.Id)}; not leaving them for lobby {Tags.Lobby(lobby)}. Closing the lobby first, then accepting again, joins.");
            string who = from.Length > 0 ? from : LobbyHostName(lobby);
            NotifyAbout(named ? request.FromSteamId : LobbyHostId(lobby), NoticeId.CantJoinHosting, who);
            return;
        }

        if (verdict == SteamJoinVerdict.AskToSwitch)
        {
            AskToSwitch(request, from);
            return;
        }

        if (!Ready())
        {
            return; // said why (a restored backup waiting for a restart, online off, Steam not up)
        }

        JoinNowFromSteam(request, from, how);
    }

    private void JoinNowFromSteam(in SteamJoinRequest request, string from, string how)
    {
        ulong lobby = request.LobbyId;
        string name = from.Length > 0 ? from : LobbyHostName(lobby);
        ulong who = from.Length > 0 ? request.FromSteamId : LobbyHostId(lobby);
        if (name.Length == 0)
        {
            name = "your friend";
        }

        ModLog.Info($"Online: joining lobby {Tags.Lobby(lobby)} from Steam ({how}); the host's versions and seat are checked as for Join Lobby.");
        NotifyAbout(who, NoticeId.JoiningLobby, name);
        if (request.Source == SteamJoinSource.Launch && Campaign.CampaignGame.Title() is { m_HasStarted: true } title && (int)title.m_CurrentButtonGroup == 0 && !title._isTransitioningGroup)
        {
            // A game started for an invite is at press-to-start, and the lobby's screen comes over the title's main group. Its failure costs the main group, not the join.
            Step("the title's press-start, for a game started for an invite", () => title.PressedToStart(null!));
        }

        BeginJoin(lobby, name, request.Source switch
        {
            SteamJoinSource.Launch => LobbyRoute.ConnectLobby,
            SteamJoinSource.JoinGame => LobbyRoute.JoinGame,
            _ => LobbyRoute.Invite,
        });
    }

    /// <summary>
    /// A Steam invite while in a lobby: the game's yes/no, Stay first. Leaving goes through the flow as any leave does; the join after it is the session's next, with everything
    /// checked again.
    /// </summary>
    private void AskToSwitch(SteamJoinRequest request, string from)
    {
        int language = LanguageIndex();
        string there = from.Length > 0 ? from : LobbyHostName(request.LobbyId);
        string here = _host != null ? SafeText.Clean(SteamFriends.GetPersonaName(), 32) : _hostName; // not DisplayNameOf: for this PC that says the one-PC test
        string text = LobbyWords.AskSwitchLobby.Text(language, DisplayName.ForRichText(here), DisplayName.ForRichText(there)); // the game's prompt has rich text on, and these are Steam names
        bool asked = LobbyQuestion.Ask(LobbyWords.Online.Text(language), text, LobbyWords.Stay.Text(language), LobbyWords.Leave.Text(language), go =>
        {
            ModLog.Info($"Lobby: the invite to lobby {Tags.Lobby(request.LobbyId)}: {(go ? "leaving for it" : "stayed")}.");
            _meter.LeavePromptAnswered(go);
            if (go)
            {
                Guarded("Steam join", () =>
                {
                    LeaveAll("leaving for an invite");
                    if (Ready())
                    {
                        JoinNowFromSteam(request, from, "an invite, after leaving the lobby");
                    }
                });
            }
        });
        if (!asked)
        {
            ModLog.Warn($"Online (Steam join): couldn't ask about lobby {Tags.Lobby(request.LobbyId)}; staying where you are.");
        }
    }

    /// <summary>
    /// Every frame: this PC's Steam rich presence follows the session (<see cref="SteamPresence"/> sends only changes): the
    /// host's lobby and player count while hosting or playing in a friend's game (also while rejoining it after a drop), so
    /// friends see it and "Join Game" works; nothing otherwise. Not for the one-PC test's friend side (it's this same game's
    /// lobby).
    /// </summary>
    private void UpdatePresence()
    {
        if (TourHoldsPresence())
        {
            ShowPresence(null); // a check tour publishes no join key and no status (OnlineSession.TourHolds.cs)
            return;
        }

        SteamPresenceState? state = null;
        if (_host != null && _link.Id != 0)
        {
            int friends = _parked.Count + _pads.Count - (_pads.ContainsKey(_self) ? 1 : 0);
            state = new SteamPresenceState(_link.Id, Hosting: true, 1 + friends, MaxLobbyMembers);
        }
        else if (_friend != null && (_friend.State == FriendSessionState.Joined || _everJoined) && !_solo && _link.Id != 0)
        {
            state = new SteamPresenceState(_link.Id, Hosting: false, Math.Max(_link.Members.Length, 2), MaxLobbyMembers);
        }

        ShowPresence(state);
    }

    /// <summary>Puts <paramref name="state"/> on Steam's rich presence when it changed, and says so in the log.</summary>
    private void ShowPresence(SteamPresenceState? state)
    {
        int refused = _presence.Refused;
        if (!_presence.Show(state))
        {
            return;
        }

        string refusal = _presence.Refused > refused ? $" (Steam refused {_presence.Refused - refused} of the keys)" : string.Empty;
        ModLog.Info(state is { } shown
            ? $"Steam: rich presence \"{SteamPresence.Status(shown)}\"; Join Game on you joins lobby {Tags.Lobby(shown.LobbyId)}{refusal}."
            : $"Steam: rich presence cleared (no session to join){refusal}.");
    }

    /// <summary>
    /// Host: tells a friend which pad family their virtual pad reads as here (after a join, a rejoin or a change); the
    /// one-PC test's friend side too, over the same path.
    /// </summary>
    private void TellPadDevice(ulong steamId, VirtualController pad) => _host?.SendPadDevice(steamId, (byte)pad.DeviceType);

    /// <summary>Host: a friend picked up another pad family mid-session: their virtual pad's button prompts follow, and they're told.</summary>
    private void PadDeviceChanged(ulong steamId, string tag, byte deviceType)
    {
        if (!_pads.TryGetValue(steamId, out RemotePad? remote) || remote.Pad is not VirtualPad pad)
        {
            return;
        }

        // Said only when the family read here changes: a device type that maps to the same gamepad family (anything that
        // isn't one reads as Xbox) changes nothing here, so a friend flipping between such values can't fill the log.
        EInputDeviceType family = VirtualPad.GamepadTypeFrom(deviceType);
        if (family != pad.DeviceType)
        {
            ModLog.Info($"Online: {tag} picked up another pad ({VirtualPad.FamilyName(family)}); their button prompts here follow it.");
            pad.SetDeviceType(family);
        }

        TellPadDevice(steamId, pad);
    }

    /// <summary>
    /// Friend, every frame: when our pad is another family than the host was told (the pad was swapped: unplugged, and
    /// another one picked up), the host is told, so its button prompts for us follow. The pad's name is kept for
    /// the status box.
    /// </summary>
    private void SyncPadDevice()
    {
        if (_friend == null || _localPad is not { IsDetached: true } pad)
        {
            return;
        }

        if (_friend.SetPadDevice((byte)pad.DeviceType))
        {
            _padName = SafeText.Clean(pad.Joystick.name, 48);
            ModLog.Info($"Online: your pad is now \"{_padName}\" ({VirtualPad.FamilyName(pad.DeviceType)}); telling {(_solo ? "the one-PC test's host" : HostTag())}, " +
                "so the button prompts for you there match it.");
        }
    }

    /// <summary>Friend: notes our pad's name for the status box (a pad found, picked up again or swapped).</summary>
    private void NotePadName()
    {
        _padName = _localPad == null ? string.Empty : SafeText.Clean(_localPad.Joystick.name, 48);
    }

    /// <summary>Friend: the host said which family it reads our pad as (logged when it changes).</summary>
    private void PadDeviceSeen(byte deviceType)
    {
        string where = _solo ? "Online one-PC test" : "Online";
        if (_localKeyboard != null)
        {
            ModLog.Info($"{where}: {(_solo ? "the host side" : $"{HostTag()}'s game")} reads your keyboard as {VirtualPad.FamilyName((EInputDeviceType)deviceType)}.");
            _nextStatus = TimeSpan.Zero;
            return;
        }

        ModLog.Info($"{where}: {(_solo ? "the host side" : $"{HostTag()}'s game")} reads your pad as {VirtualPad.FamilyName(VirtualPad.GamepadTypeFrom(deviceType))} " +
            $"(button prompts for you there); your pad here is \"{_padName}\".");
        _nextStatus = TimeSpan.Zero;
    }

    /// <summary>
    /// Friend: the status box's line about our pad: which pad the mod reads and sends, and which family the host
    /// reads it as once the host has said. Made again only when one of those changed.
    /// </summary>
    private string PadLine()
    {
        if (_friend != null && _localKeyboard != null)
        {
            // A keyboard has no family to swap: which keys go, and that the host reads them as a keyboard.
            int keys = _localKeyboard.Controls.Elements.Count;
            return _friend.HostDeviceType < 0 ? $"   your keyboard: {keys} keys and buttons go to {(_solo ? "the host side" : _hostName)}"
                : $"   your keyboard: {keys} keys and buttons · {(_solo ? "read" : $"{_hostName}'s game reads them")} as a keyboard";
        }

        if (_friend == null || _localPad == null)
        {
            return string.Empty;
        }

        (string Name, byte Ours, int Theirs, string Host) now = (_padName, _friend.DeviceType, _friend.HostDeviceType, _hostName);
        if (now == _padLineFor && _padLine.Length > 0)
        {
            return _padLine;
        }

        _padLineFor = now;
        string name = now.Name.Length > 0 ? now.Name : "a gamepad";
        string ours = VirtualPad.FamilyName(VirtualPad.GamepadTypeFrom(now.Ours));
        _padLine = now.Theirs < 0 ? $"   your pad: {name} ({ours})"
            : $"   your pad: {name} · {(_solo ? "read" : $"{now.Host}'s game reads it")} as {VirtualPad.FamilyName(VirtualPad.GamepadTypeFrom((byte)now.Theirs))}";
        return _padLine;
    }
}
#endif
