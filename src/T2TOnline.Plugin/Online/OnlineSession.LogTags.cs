#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// The log tags (D127). Every PC in a lobby names players and the lobby in its log by tags under one key: the one its host
// makes for the hosting session and publishes as lobby data (t2t.logtag), kept when the host has to make a new lobby. So
// two PCs' logs of one lobby name everyone alike, and neither holds a Steam name or ID. Each PC says once per lobby which
// tag is its own and which is the host's ("Log tags: this PC is ..."), and once per member what shape their Steam name
// has. Outside a lobby, and in a lobby whose host published no usable key, the launch's own key is used (and the line
// says the tags won't match). The key is never logged. Steam names are registered with the log's backstop at every read
// (SteamFriends.NameRead) and from the lobby board, so a line that still writes a name gets its tag instead.
internal sealed partial class OnlineSession
{
    private readonly HashSet<ulong> _describedNames = new();
    private byte[]? _logKey;
    private ulong _taggedLobby;
    private ulong _taggedHost;
    private ulong[] _taggedMembers = Array.Empty<ulong>();

    /// <summary>
    /// Every persona name Steam gives the mod is registered with the log's backstop, and the Steam library's own lines name
    /// players and lobbies by the tags in use.
    /// </summary>
    private static void InstallLogTags()
    {
        SteamFriends.NameRead = ModLog.RegisterName;
        SteamLogTags.PlayerTag = Tag;
        SteamLogTags.LobbyTag = static lobby => ModLog.Scrub.Tags.Lobby(lobby);
    }

    /// <summary>Host: a new hosting session's key, used from now on and published with the lobby data.</summary>
    private void NewLogTagKey()
    {
        _logKey = LogTags.NewKey();
        UseLogTags(new LogTags(_logKey, shared: true), _link.Id, _self);
    }

    /// <summary>Host: the hosting session's key goes up with the lobby data (a refusal is logged without the value).</summary>
    private void PublishLogTagKey(ulong lobby)
    {
        if (_logKey != null)
        {
            SetLobbyData(lobby, LobbyDataKeys.LogTag, LogTags.ToHex(_logKey));
        }
    }

    /// <summary>
    /// Friend: the host's key from the lobby data (untrusted: exactly 32 hex digits), or this launch's own when there's none.
    /// A host (the one-PC test's friend joins its own lobby) keeps its own.
    /// </summary>
    private void ReadLogTagKey(ulong lobby, ulong host)
    {
        if (_host != null)
        {
            return;
        }

        string published = SteamLobbies.GetLobbyData(lobby, LobbyDataKeys.LogTag);
        if (LogTags.TryParseKey(published, out byte[] key))
        {
            UseLogTags(new LogTags(key, shared: true), lobby, host);
            return;
        }

        UseLogTags(ModLog.LaunchTags, lobby, host);
        ModLog.Warn("Log tags: the host's lobby " + (published.Length == 0 ? "has no log tag key (an older T2T Online?)" : "has a log tag key that isn't 32 hex digits") +
            ", so tags here won't match the host's log.");
    }

    /// <summary>
    /// Members as the lobby lists them (both PCs see the same list): the tags widen if two collide, each member's name shape
    /// is written once, and a lobby made anew (the host's, or the one a friend followed it to) gets its own tags line.
    /// </summary>
    private void NoteLogTagMembers()
    {
        if (_taggedLobby == 0)
        {
            return;
        }

        LogTags tags = ModLog.Scrub.Tags;
        if (_link.Id != 0 && _link.Id != _taggedLobby)
        {
            UseLogTags(tags, _link.Id, _taggedHost);
        }

        ulong[] members = _link.Members;
        if (!SameMembers(members, _taggedMembers))
        {
            _taggedMembers = members;
            if (tags.AddMembers(members))
            {
                ModLog.Info($"Log tags: two members' tags in lobby {tags.Lobby(_taggedLobby)} begin with the same {LogTags.ShortDigits} digits, " +
                    $"so tags have {LogTags.LongDigits} from now on; this PC is {tags.Player(_self)}, host {tags.Player(_taggedHost)}.");
            }
        }

        foreach (ulong member in members)
        {
            if (member != 0 && !_describedNames.Contains(member))
            {
                DescribeName(tags, member);
            }
        }
    }

    /// <summary>
    /// The lobby board's names, as the host knows them: registered with the log's backstop, for the lobby's members only. A
    /// board is the host's word, so a seat naming someone outside the lobby (the one-PC test's stand-in, or a modified
    /// host's invented players) pins no name to an ID that isn't here, nor grows the registry.
    /// </summary>
    private void RegisterBoardNames(LobbyBoardMessage board)
    {
        ulong[] members = _link.Members.Length > 0 ? _link.Members : SteamLobbies.GetMembers(_link.Id);
        foreach (LobbyBoardSeat seat in board.Seats)
        {
            if (Array.IndexOf(members, seat.SteamId) >= 0)
            {
                ModLog.RegisterName(seat.SteamId, seat.Name);
            }
        }
    }

    /// <summary>The session's lobby is left: the launch's own tags again.</summary>
    private void EndLogTags()
    {
        bool wasTagged = _taggedLobby != 0;
        ModLog.Scrub.Tags = ModLog.LaunchTags;
        _logKey = null;
        _taggedLobby = 0;
        _taggedHost = 0;
        _taggedMembers = Array.Empty<ulong>();
        _describedNames.Clear();
        if (wasTagged)
        {
            ModLog.Info("Log tags: out of the lobby; this PC's own tags from here on (they differ from the lobby's).");
        }
    }

    private static bool SameMembers(ulong[] a, ulong[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Array.IndexOf(b, a[i]) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The tags in use from now on, and the line that says whose they are when the lobby or the key changed.</summary>
    private void UseLogTags(LogTags tags, ulong lobby, ulong host)
    {
        bool changed = !ReferenceEquals(ModLog.Scrub.Tags, tags) || lobby != _taggedLobby;
        ModLog.Scrub.Tags = tags;
        _taggedHost = host;
        if (!changed)
        {
            return;
        }

        _taggedLobby = lobby;
        _taggedMembers = Array.Empty<ulong>();
        _describedNames.Clear();
        ModLog.Info($"Log tags: this PC is {tags.Player(_self)} in lobby {tags.Lobby(lobby)}, host {tags.Player(host)}" +
            (tags.Shared ? string.Empty : " (this PC's own tags: they won't match the host's log)") +
            "; names, Steam IDs and folders are never written to this log, and tags change with each lobby.");
    }

    /// <summary>A member's name shape, once per lobby; a name Steam doesn't know yet is tried again at the next poll.</summary>
    private void DescribeName(LogTags tags, ulong member)
    {
        string name = member == _self ? SteamFriends.GetPersonaName() : SteamFriends.GetFriendPersonaName(member);
        if (name.Length == 0 || name == DisplayName.SteamUnknown)
        {
            return;
        }

        _describedNames.Add(member);
        ModLog.Info("Log tags: " + NameShape.Describe(tags.Player(member), name, NativeLobby.LobbyScreens.CanDraw));
    }
}
#endif
