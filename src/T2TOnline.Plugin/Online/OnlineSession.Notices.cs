#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Online;

// What the player is told, through one router (Core/Lobby/Notices.cs has the table): a player sees nothing about networking unless it's wrong, and then in
// the game's own toast or one-button prompt, in their language. Every notice is logged. When the native screens can't carry one (none wired in, the NativeLobby
// setting off, the shell off for this game, or the screens failing), it falls back to the status box, so it's never lost; the box itself is a developer's tool
// (the ShowStatusBox setting), which shows every notice, whatever its kind. The screen names players by their Steam names, as on a couch;
// the log's copy of a notice names them by their tags (D127): a site that names someone says whom (NotifyAbout, NotifyText).
internal sealed partial class OnlineSession
{
    /// <summary>How long a notice stays in the status box.</summary>
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(8);

    /// <summary>A notice that came before the title's first frame (the saves' start-up recovery, a loader's note) waits this long at most for it.</summary>
    private static readonly TimeSpan EarlyNoticeFor = TimeSpan.FromSeconds(120);

    private const int MaxEarlyNotices = 12;

    /// <summary>How often the title is looked for while notices wait for it: it's a search of the scene.</summary>
    private static readonly TimeSpan EarlyLookInterval = TimeSpan.FromMilliseconds(250);

    private readonly List<(NoticeId Id, NoticeText Text)> _earlyNotices = new();
    private readonly Dictionary<string, TimeSpan> _noticeLimits = new(StringComparer.Ordinal);
    private bool _titleSeen;
    private bool _outOfLobbySaid;
    private TimeSpan _outOfLobbyAt = TimeSpan.FromHours(-1);
    private TimeSpan _noticesSince = TimeSpan.MinValue;
    private TimeSpan _nextEarlyLook;
    private bool _startUpNoticesTaken;

    /// <summary>
    /// Tells the player something that names nobody. <paramref name="args"/> are the word's arguments, or a log-only notice's one text. Never throws. A notice
    /// that names a player goes through <see cref="NotifyAbout"/> or <see cref="NotifyText"/>, so its log copy can name them by their tag.
    /// </summary>
    internal void Notify(NoticeId id, params string[] args) => Route(id, NoticeText.Same(args ?? Array.Empty<string>()));

    /// <summary>
    /// Tells the player something about <paramref name="who"/>: the word's first argument is their <paramref name="name"/> (cleaned of anything a peer could use to
    /// draw markup) on the screen, their tag in the log; <paramref name="more"/> are the word's next arguments. Never throws.
    /// </summary>
    internal void NotifyAbout(ulong who, NoticeId id, string name, params string[] more)
    {
        var args = new string[1 + (more?.Length ?? 0)];
        args[0] = name;
        more?.CopyTo(args, 1);
        var logArgs = (string[])args.Clone();
        logArgs[0] = who == 0 ? "a player" : Tag(who);
        Route(id, new NoticeText(args, null, logArgs, null));
    }

    /// <summary>A log-only or free-text notice that names someone: <paramref name="text"/> for the screen, <paramref name="logText"/> (tags, not names) for the log. Never throws.</summary>
    internal void NotifyText(NoticeId id, string text, string logText) => Route(id, new NoticeText(new[] { text }, null, new[] { logText }, null));

    /// <summary>Tells the player something whose line the site made (a refusal's reason picks its word), with its log copy's line. Never throws.</summary>
    internal void Notify(NoticeId id, LobbyLine line, LobbyLine logLine) => Route(id, new NoticeText(Array.Empty<string>(), line, Array.Empty<string>(), logLine));

    /// <summary>
    /// A failure's message (a join that failed, a session that ended) on the native screens, or in the status box when they can't. The caller logs the reason; this logs
    /// where it went, with <paramref name="logMessage"/>'s words (the same message naming the host by tag, not name).
    /// </summary>
    private void NotifyFailure(FailureMessage message, FailureMessage logMessage)
    {
        string english = message.Line.Text(0);
        bool shown = Context().ScreensReady && TryScreens("showing a failure", s => s.ShowFailure(message));
        if (!shown || ModSettings.ShowStatusBox)
        {
            ShowInBox(english);
        }

        ModLog.Info($"Notice (a failure, {message.Reason}): {(shown ? "on the native screen" : "in the status box")}: {logMessage.Line.Text(0)}");
    }

    /// <summary>
    /// The parts that tell the player a failure of theirs: a friend's game that couldn't follow the host's (a screen, or the match), a part of the campaign off. Once, from the constructor.
    /// </summary>
    private void InstallNoticeSources()
    {
        _campaign.FollowFailed = () => NotifyLimited(NoticeId.FollowFailed, _hostSteamId, _hostName);
        _campaign.FriendFollowFailed = friend => NotifyLimited(NoticeId.FriendFollowFailed, friend, DisplayNameOf(friend));
        _campaign.ModuleSwitchedOff = () => NotifyLimited(NoticeId.CampaignPartOff, 0, string.Empty);
        _relay.PlayFailed = FriendMatchFailedHere; // asked on the game's prompt where it can be (OnlineSession.FollowQuestion.cs), else this toast
        _relay.FriendLoadFailed = friend => NotifyLimited(NoticeId.FriendFollowFailed, friend, DisplayNameOf(friend));
    }

    /// <summary>
    /// Like <see cref="NotifyAbout"/> (<see cref="Notify(NoticeId, string[])"/> when <paramref name="who"/> is 0: nobody), but the same notice about the same player
    /// isn't told again within a minute: a failure that comes with every retry says so once.
    /// </summary>
    private void NotifyLimited(NoticeId id, ulong who, string name)
    {
        string key = $"{id}:{who}:{name}";
        TimeSpan now = _clock.Elapsed;
        if (_noticeLimits.TryGetValue(key, out TimeSpan last) && now - last < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _noticeLimits[key] = now;
        if (who == 0)
        {
            Notify(id, name);
        }
        else
        {
            NotifyAbout(who, id, name);
        }
    }

    /// <summary>After a session the pad didn't go back to the game: said, the only way back being to plug it again.</summary>
    private void NotifyPadBack()
    {
        if (!_padBack)
        {
            Notify(NoticeId.PadNotBack);
        }
    }

    /// <summary>
    /// The state the router weighs a notice against. In play is where a prompt would sit over the game: a friend following the host (their own UI is locked, so nothing could
    /// answer it), the host with a match on, or the host's Start under way at the title (the game's own next menu would come over it, and nothing here would be read). The host on
    /// the world map, or at an idle title, isn't: those are menus, and a prompt is one more.
    /// </summary>
    private NoticeContext Context() => new(
        ScreensReady: ScreensReady,
        InPlay: _flow.PlayActive || _flow.Host == LobbyHostState.Starting ||
            (_flow.Host is LobbyHostState.GameFlow or LobbyHostState.InGame && (!MatchSetup.CurrentMatch(out _).IsNone || Campaign.CampaignGame.TitleLoaded())),
        LobbyUp: LobbyShell.IsShown && LobbyShell.Kind == ShellKind.Lobby,
        AtTitle: Campaign.CampaignGame.TitleLoaded());

    /// <summary>The native screens can carry what's said: they're wired in, the setting is on, and they haven't failed this game.</summary>
    private bool ScreensReady => Screens != null && ModSettings.NativeLobby && LobbyShell.Available;

    private void Route(NoticeId id, NoticeText text)
    {
        try
        {
            TourNotices?.Add(id);
            NoticeSpec spec = Notices.Of(id);
            if (spec.IsShown && !_titleSeen && !_gameClosing)
            {
                // Before the title's first frame there's no toast queue or prompt to be had: it waits for the title (the status box is for the notice it can't show).
                if (_earlyNotices.Count < MaxEarlyNotices)
                {
                    _earlyNotices.Add((id, text));
                    ModLog.Info($"Notice {id}: waits for the title screen ({Notices.English(spec, text.LogArgs, text.LogLine)}).");
                }
                else
                {
                    ModLog.Info($"Notice {id}: not shown: {MaxEarlyNotices} notices already wait for the title screen ({Notices.English(spec, text.LogArgs, text.LogLine)}).");
                }

                return;
            }

            Deliver(spec, text);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Notice {id} failed ({ModLog.SafeErrorText(ex)}); the session goes on.");
        }
    }

    private void Deliver(NoticeSpec spec, NoticeText text)
    {
        string[] args = text.Args;
        if (args.Length < spec.Args && !spec.Dynamic)
        {
            ModLog.Warn($"Notice {spec.Id}: {args.Length} argument(s) given, {spec.Args} wanted.");
        }

        NoticeContext context = Context();
        NoticeDelivery delivery = Notices.Deliver(spec, context);
        string english = Notices.English(spec, args, text.Line);
        string logged = Notices.English(spec, text.LogArgs, text.LogLine); // the same words, naming players by tag
        LobbyLine line = text.Line ?? Notices.LineOf(spec, args);
        bool shown = delivery switch
        {
            NoticeDelivery.Toast => TryToast(line, Notices.TimingFor(spec, context)),
            NoticeDelivery.Prompt => TryScreens("showing a notice", s => s.ShowFailure(Notices.Prompt(line))),
            _ => true,
        };

        // The status box carries what the screens couldn't, and, for a developer, everything. With no screens it's all a player has to go by, so what the lobby's screen would have shown goes there too.
        bool box = delivery == NoticeDelivery.StatusBox || !shown || ModSettings.ShowStatusBox || !context.ScreensReady;
        string where = !shown ? "the screens couldn't, so the status box" : delivery switch
        {
            NoticeDelivery.None => spec.IsShown ? "no screen (the lobby says it)" : "no screen",
            NoticeDelivery.StatusBox => "the status box (the native screens are off)",
            NoticeDelivery.Prompt => "a native prompt",
            _ => $"a native toast ({Notices.TimingFor(spec, context)})",
        };
        ModLog.Info($"Notice {spec.Id} ({spec.Kind}): {where}: {logged}");
        if (box && english.Length > 0)
        {
            ShowInBox(english);
        }
    }

    private bool TryToast(LobbyLine line, ToastTiming timing)
    {
        bool queued = false;
        return TryScreens("showing a toast", s => queued = s.ShowNotice(line, timing)) && queued;
    }

    /// <summary>
    /// A call into the native screens, with the toast mark on (<see cref="OwnToasts"/>: whatever it queues is the mod's own, not the game's run toast). False when there are no
    /// screens or they failed, which is logged: the session never depends on them.
    /// </summary>
    private bool TryScreens(string what, Action<ILobbyScreens> call)
    {
        ILobbyScreens? screens = Screens;
        if (screens == null || _gameClosing)
        {
            return false;
        }

        try
        {
            using (OwnToasts.Begin())
            {
                call(screens);
            }

            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the screens failed at {what} ({ModLog.SafeErrorText(ex)}); the session goes on.");
            return false;
        }
    }

    /// <summary>Puts a line in the status box for a few seconds (the fallback, and the developer's box).</summary>
    private void ShowInBox(string text)
    {
        _notice = text;
        _noticeUntil = _clock.Elapsed + NoticeFor;
        _nextStatus = TimeSpan.Zero;
    }

    /// <summary>Every frame: the notices that waited for the title are told once it's up, and the host's Steam connection going is told once per drop.</summary>
    private void TickNotices(TimeSpan elapsed)
    {
        if (!_startUpNoticesTaken)
        {
            // The saves' start-up recovery ran before this session existed, and what it found is the first thing a player must read: said like any other, when the title is up.
            _startUpNoticesTaken = true;
            foreach (NoticeId id in Campaign.SaveBackups.TakeStartUpNotices())
            {
                Notify(id);
            }
        }

        if (!_titleSeen && elapsed >= _nextEarlyLook)
        {
            _nextEarlyLook = elapsed + EarlyLookInterval;
            if (_noticesSince == TimeSpan.MinValue)
            {
                _noticesSince = elapsed;
            }

            // The title's warm-ups over, not just its scene in: they open and close menus of their own, and its loading screen is still up.
            if (LobbyShell.TitleAllowingOpen() != null || elapsed - _noticesSince > EarlyNoticeFor)
            {
                _titleSeen = true;
                var waiting = new List<(NoticeId Id, NoticeText Text)>(_earlyNotices);
                _earlyNotices.Clear();
                foreach ((NoticeId id, NoticeText text) in waiting)
                {
                    Route(id, text);
                }
            }
        }

        // The status box used to say "friends can't join right now: reconnecting to Steam": now a toast, once per drop (and not more than once in half a minute).
        bool outOfLobby = _host != null && !_solo && _link.OutOfLobby;
        if (outOfLobby != _outOfLobbySaid)
        {
            _outOfLobbySaid = outOfLobby;
            if (outOfLobby && elapsed - _outOfLobbyAt > TimeSpan.FromSeconds(30))
            {
                _outOfLobbyAt = elapsed;
                Notify(NoticeId.OutOfLobby);
            }
        }
    }

    /// <summary>A notice's words: what the screen shows (names) and what the log writes (tags; the same words otherwise).</summary>
    private sealed record NoticeText(string[] Args, LobbyLine? Line, string[] LogArgs, LobbyLine? LogLine)
    {
        /// <summary>A notice that names nobody: the log writes what the screen shows.</summary>
        public static NoticeText Same(string[] args) => new(args, null, args, null);
    }
}
#endif
