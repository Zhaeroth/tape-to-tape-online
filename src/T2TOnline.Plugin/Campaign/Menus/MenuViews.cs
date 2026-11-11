#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The menu views (D43): menus are views of the host's menus. The host's menus, their data, the shared cursor,
/// list selectors, the menus' steps, clicks and rerolled offers go to every friend as <see cref="MenuEvent"/>s
/// (<see cref="MessageType.MenuOpen"/>, <see cref="MessageType.MenuNav"/>, <see cref="MessageType.MenuSelect"/>,
/// <see cref="MessageType.MenuClick"/>, channel 1), with what doesn't fit a menu's data (the run-end menu's run summary)
/// as a <see cref="MenuAttachment"/> after its open (<see cref="MessageType.MenuAttachment"/>, the same channel, so it
/// comes after the open); each friend's game shows them (<see cref="FriendMenus"/>), as the
/// real menu or as the spectator panel. The aim (D43) is that everyone sees the same menus, so selections can be made and
/// everyone can see the same thing and discuss it. Only the host's game acts on a choice.
/// <para>
/// Every entry is guarded here, so the relay never switches this module off with a panel or a real menu still up on a
/// friend's screen (a module it switches off gets no stop). After <see cref="MaxErrors"/> failures within
/// <see cref="ErrorWindowSeconds"/> the views switch themselves off until the next online session: friends are told every
/// menu closed, and a friend closes what it shows. A new session (hosting or following) turns them on again.
/// </para>
/// </summary>
internal sealed class MenuViews : ICampaignModule
{
    private const int MaxLoggedMalformed = 5;
    private const int MaxErrors = 5;
    private const double ErrorWindowSeconds = 300;
    private const int MaxErrorLogs = 3;

    private static readonly MessageType[] Types = { MessageType.MenuOpen, MessageType.MenuNav, MessageType.MenuSelect, MessageType.MenuClick, MessageType.MenuAttachment };

    private readonly List<MenuEvent> _outbox = new();
    private readonly List<HostAttachment> _attachments = new();
    private readonly MenuMirror _mirror = new();
    private readonly BitWriter _scratch = new(1024);
    private readonly NoticeSink _notify;
    private readonly Action _hostUpdate;
    private readonly Action _friendUpdate;
    private readonly Action _sweep;
    private CampaignRelay? _relay;
    private int _malformed;
    private int _oversize;
    private long _sent;
    private long _received;
    private int _errors;
    private double _errorsSince = double.NegativeInfinity;
    private bool _off;

    /// <summary>Made once by <see cref="CampaignModules"/>; <paramref name="context"/> gives the status-box notices.</summary>
    public MenuViews(CampaignContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _notify = context.Notify;
        Host = new HostMenus(_outbox.Add, _attachments.Add);
        Friend = new FriendMenus(_mirror, context.Matches, context.Notify);
        _hostUpdate = () =>
        {
            Host.Update(HasFriends);
            Flush();
        };
        _friendUpdate = Friend.Update;
        _sweep = Friend.Sweep;
        Instance = this;
    }

    /// <summary>The module of the running game, or null before it's made.</summary>
    public static MenuViews? Instance { get; private set; }

    /// <summary>Host: the menu messages sent to friends since hosting started (read by the check tour).</summary>
    public long Sent => _sent;

    /// <inheritdoc />
    public string Name => "Menus";

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Owns => Types;

    /// <summary>True once the views switched themselves off until the next session (too many failures).</summary>
    public bool Off => _off;

    /// <summary>True while this PC hosts.</summary>
    public bool Hosting => _relay?.Hosting == true;

    /// <summary>
    /// True while this PC hosts with at least one friend in, and its campaign is shared (on its co-op slot, D48): a host
    /// still on its solo run (hosting started mid-run) shares none of its menus.
    /// </summary>
    public bool HasFriends => _relay is { Hosting: true } relay && relay.Friends.Count > 0 && RunSync.Instance?.HostOnCoopSlot == true;

    /// <summary>True while this PC follows a host.</summary>
    public bool Following => _relay?.Following == true;

    /// <summary>The host's side.</summary>
    public HostMenus Host { get; }

    /// <summary>The friend's side.</summary>
    public FriendMenus Friend { get; }

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay)
    {
        _relay = relay;
        NewSession();
        Host.Tally.Clear();
        _ = Host.TakeLinesLeftOut();
        Run("starting to host", () =>
        {
            Host.Clear();
            _outbox.Clear();
            _attachments.Clear();
        });
    }

    /// <inheritdoc />
    public void StopHosting(string reason)
    {
        if (_sent > 0)
        {
            ModLog.Info($"Menus: stopped hosting ({reason}); {_sent} menu message(s) sent, {_oversize} too big to send.");
        }

        if (Host.Tally.Any)
        {
            ModLog.Info(Host.Tally.Line(_oversize, Host.TakeLinesLeftOut()));
        }

        Run("stopping hosting", Host.Clear);
        _outbox.Clear();
        _attachments.Clear();
        _sent = 0;
        _oversize = 0;
    }

    /// <inheritdoc />
    public void FriendJoined(ulong friend) => Run("sending the open menus to a friend who joined", () =>
    {
        if (_off)
        {
            return;
        }

        if (!HasFriends)
        {
            // The host's game plays its solo run (D48): its menus aren't shared, and none goes on the board for this friend.
            // They're only told nothing is open (a friend rejoining closes what it still showed).
            MenuEvent none = MenuEvent.Synced(Array.Empty<uint>());
            _ = _relay?.SendTo(friend, none.Type, Channel.Events, w => MenuMessages.Write(w, none));
            ModLog.Info($"Menus: {LogNames.Tag(friend)} joined while the host plays its solo run; its menus aren't shared.");
            return;
        }

        Flush();
        IReadOnlyList<MenuEvent> open = Host.Join();
        int sent = 0;
        foreach (MenuEvent menuEvent in open)
        {
            if (Fits(menuEvent) && _relay != null && _relay.SendTo(friend, menuEvent.Type, Channel.Events, w => MenuMessages.Write(w, menuEvent)))
            {
                sent++;
            }
        }

        string whoJoined = $"{LogNames.Tag(friend)}, who joined";
        int menus = open.Count(e => e.Kind == MenuEventKind.Opened);
        if (menus > 0 || sent < open.Count)
        {
            ModLog.Info($"Menus: sent the host's {menus} open menu(s) to {whoJoined} ({sent} of {open.Count} message(s)).");
        }

        // Then what goes with them (the same channel: after their opens).
        foreach (HostAttachment attachment in Host.Attachments())
        {
            bool delivered = FitsAttachment(attachment) && _relay != null &&
                _relay.SendTo(friend, MessageType.MenuAttachment, Channel.Events, w => MenuAttachmentMessage.Write(w, attachment.Attachment));
            CountAttachment(attachment, delivered);
            LogAttachment(attachment, delivered ? whoJoined : null, FriendTags());
        }
    });

    /// <inheritdoc />
    public void StartFollowing(CampaignRelay relay)
    {
        _relay = relay;
        _malformed = 0;
        _received = 0;
        NewSession();
        Run("starting to follow", () =>
        {
            _mirror.Clear();
            if (!_off)
            {
                Friend.Start(relay.HostName);
            }
        });
    }

    /// <inheritdoc />
    public void StopFollowing(string reason, bool gameClosing)
    {
        Run("stopping following", () => Friend.Stop(reason, gameClosing));
        _mirror.Clear();
        if (_received > 0)
        {
            ModLog.Info($"Menus: stopped following ({reason}); {_received} menu message(s) received, {_malformed} malformed.");
        }
    }

    /// <inheritdoc />
    public bool Receive(MessageType type, ref BitReader reader)
    {
        if (!Following)
        {
            return false;
        }

        if (_off)
        {
            return true; // taken and dropped: the views are off until the next session
        }

        if (type == MessageType.MenuAttachment)
        {
            return ReceiveAttachment(ref reader);
        }

        if (!MenuMessages.TryRead(type, ref reader, out MenuEvent? menuEvent))
        {
            _malformed++;
            if (_malformed <= MaxLoggedMalformed)
            {
                ModLog.Warn($"Menus: a malformed {type} message from the host was dropped{(_malformed == MaxLoggedMalformed ? " (further ones are only counted)" : string.Empty)}.");
            }

            return false;
        }

        _received++;
        MenuEvent read = menuEvent!;
        MenuMirrorChange change = _mirror.Apply(read);
        Run($"showing the host's {read.Kind}", () => Friend.Apply(change, read));
        return true;
    }

    /// <inheritdoc />
    public void Update()
    {
        if (_off)
        {
            return;
        }

        if (Hosting)
        {
            Run("reading the host's menus", _hostUpdate);
        }

        if (Following)
        {
            Run("showing the host's menus", _friendUpdate);
        }
    }

    /// <summary>
    /// Every frame of the game, session or not (from <see cref="MenuPatches"/>): a menu this PC asked to open for the host
    /// that only opened after the host closed it, or after the session ended, is closed once it's up.
    /// </summary>
    public void Tick()
    {
        if (Friend.Expecting && (_off || !Following))
        {
            Run("closing late menus", _sweep);
        }
    }

    /// <summary>
    /// Switches the views off until the next online session: friends get every open menu's close; a friend closes
    /// every real view and the panel. The game itself is unaffected.
    /// </summary>
    public void SwitchOff(string why)
    {
        if (_off)
        {
            return;
        }

        _off = true;
        ModLog.Error($"Menus: {why}; the menu views are off until the next online session (the game itself is unaffected). " +
            "Friends see no more host menus until then (a new online game turns them on again).");
        Guarded(() =>
        {
            if (Hosting)
            {
                _outbox.Clear();
                _attachments.Clear();
                foreach (MenuEvent closed in Host.CloseAll())
                {
                    _relay!.Broadcast(closed.Type, Channel.Events, w => MenuMessages.Write(w, closed));
                }
            }
        });
        Guarded(() =>
        {
            Friend.Stop("the menu views switched off", gameClosing: false);
            _mirror.Clear();
        });
        Guarded(() => _notify(NoticeId.MenusFailed));
    }

    /// <summary>
    /// Sends what the host's menus did since the last frame, to every friend: the events, then the attachments (each
    /// queued after its menu's open, and one channel keeps the order).
    /// </summary>
    private void Flush()
    {
        if (_outbox.Count == 0 && _attachments.Count == 0)
        {
            return;
        }

        foreach (MenuEvent menuEvent in _outbox)
        {
            if (Fits(menuEvent) && _relay != null)
            {
                _relay.Broadcast(menuEvent.Type, Channel.Events, w => MenuMessages.Write(w, menuEvent));
                _sent++;
            }
        }

        _outbox.Clear();
        foreach (HostAttachment attachment in _attachments)
        {
            int friends = FitsAttachment(attachment) && _relay != null
                ? _relay.Broadcast(MessageType.MenuAttachment, Channel.Events, w => MenuAttachmentMessage.Write(w, attachment.Attachment))
                : 0;
            _sent += friends > 0 ? 1 : 0;
            CountAttachment(attachment, friends > 0);
            if (friends == 0 || attachment.Attachment.Kind != MenuAttachmentKind.NurseSteps)
            {
                // The Nurse's steps go with each press on its procedure screen: NurseHost logs each step instead.
                string names = FriendTags();
                LogAttachment(attachment, friends > 0 ? (friends == _relay?.Friends.Count ? names : $"{friends} of {names}") : null, names);
            }
        }

        _attachments.Clear();
    }

    /// <summary>A friend's side: the host's attachment for an open menu. False when it's malformed (logged, the first few).</summary>
    private bool ReceiveAttachment(ref BitReader reader)
    {
        if (!MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? attachment))
        {
            _malformed++;
            if (_malformed <= MaxLoggedMalformed)
            {
                ModLog.Warn($"Menus: a malformed {MessageType.MenuAttachment} message from the host was dropped{(_malformed == MaxLoggedMalformed ? " (further ones are only counted)" : string.Empty)}.");
            }

            return false;
        }

        _received++;
        MenuAttachment read = attachment!;
        Run($"taking the host's {read}", () => Friend.Attached(read));
        return true;
    }

    /// <summary>True when the attachment fits one message (always, for one the host could make).</summary>
    private bool FitsAttachment(HostAttachment attachment)
    {
        _scratch.Reset();
        if (MenuAttachmentMessage.Write(_scratch, attachment.Attachment))
        {
            return true;
        }

        _oversize++;
        ModLog.Warn($"Menus: {attachment.MenuId}'s {attachment.Attachment} is too big to send; friends see it as text.");
        return false;
    }

    /// <summary>The friends in the session, by tag (D127), for a line that says who got something ("none" when there are none).</summary>
    private string FriendTags() =>
        _relay is { Friends.Count: > 0 } relay ? string.Join(", ", relay.Friends.Select(LogNames.Tag)) : "none";

    /// <summary>The session's tally of what went to friends.</summary>
    private void CountAttachment(HostAttachment attachment, bool delivered)
    {
        if (delivered)
        {
            Host.Tally.Attachment(attachment.Attachment.Kind.ToString());
        }
        else
        {
            Host.Tally.NoFriend();
        }
    }

    /// <summary>
    /// One line per attachment sent: to whom, its size, and what it is. <paramref name="to"/>: null when it went to nobody;
    /// <paramref name="inSession"/>: who was in, by tag.
    /// </summary>
    private static void LogAttachment(HostAttachment attachment, string? to, string inSession)
    {
        MenuAttachment sent = attachment.Attachment;
        if (to == null)
        {
            ModLog.Warn($"Menus: the {sent} ({attachment.MenuId}) went to no friend (in the session: {inSession}); they see it as text.");
            return;
        }

        ModLog.Info($"Menus: sent the host's {sent} ({attachment.MenuId}; {sent.DeflatedLength} bytes deflated; {attachment.About}) to {to}.");
    }

    /// <summary>True when the event fits one message (checked before sending: a broadcast can't be taken back).</summary>
    private bool Fits(MenuEvent menuEvent)
    {
        _scratch.Reset();
        if (MenuMessages.Write(_scratch, menuEvent))
        {
            return true;
        }

        _oversize++;
        ModLog.Warn($"Menus: {menuEvent} is over {MenuMessages.MaxMessageBytes / 1024} KB; friends won't get it.");
        return false;
    }

    /// <summary>Runs one entry; a failure is logged (the first few), counted, and after <see cref="MaxErrors"/> switches the views off.</summary>
    private void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            double now = Streaming.FrameClock.Seconds;
            if (now - _errorsSince > ErrorWindowSeconds)
            {
                _errors = 0; // failures far apart don't add up
                _errorsSince = now;
            }

            _errors++;
            if (_errors <= MaxErrorLogs)
            {
                ModLog.Error($"Menus: failed while {what}{(_errors == MaxErrorLogs ? " (further failures are only counted)" : string.Empty)}", ex);
            }

            if (_errors >= MaxErrors)
            {
                SwitchOff($"{MaxErrors} failures (the last while {what})");
            }
        }
    }

    /// <summary>A new online session: failures counted in the last one don't carry over, and views switched off by them are on again.</summary>
    private void NewSession()
    {
        _errors = 0;
        _errorsSince = double.NegativeInfinity;
        if (_off)
        {
            _off = false;
            ModLog.Info("Menus: the menu views are on again for this session (they had switched off after failures in the last one).");
        }

        MenuPatches.NewSession();
    }

    private static void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error("Menus: failed while switching off", ex);
        }
    }
}
#endif
