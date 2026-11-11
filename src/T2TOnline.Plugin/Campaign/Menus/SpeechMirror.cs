#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The speech mirror (v1). Every line of speech in this game waits for a press ("UI Confirm", polled from every Rewired player),
/// which a friend can never give, so a friend's speech stays on its first line. Here the host's presses and line starts on each
/// mirrored speaker go to friends as its <see cref="SpeechProgress"/>, and each friend's game replays them on its own bubble with the
/// game's own <c>SpeechBubbleController.Skip(anchor)</c>, one press a frame at most (<see cref="SpeechFollow"/>).
/// <list type="bullet">
/// <item>A speaker is a speech bubble anchor of a speech controller, in a slot (0 to 7) of a menu on the board (or of a
/// named context whose values the caller carries). Built in: the event NPC (slot 0, <see cref="EventSpeech"/>) and the
/// Canada curse's Pucklin (0) and Hatfreeze (1).</item>
/// <item>Host: <see cref="HostBegin(AbstractMenu, int, SpeechBubbleController, SpeechBubbleAnchor, string)"/> when the
/// speaker's speech starts; each frame its newest bubble's resolved line object is read (a new one is the next line); each
/// press the game takes on its bubble (<c>SpeechBubbleView.RequestSkip</c>, <see cref="HostPressed"/>) raises the line's
/// stage; the value goes on the menu's board (<see cref="HostMenus.SpeechValue"/>), joins included.</item>
/// <item>Friend: <see cref="FriendBegin(uint, int, SpeechBubbleController, SpeechBubbleAnchor, string, int)"/> on its own
/// copy of the speaker; each frame the host's value is read from the mirrored menu and this PC's bubble driven. A press that
/// isn't the mod's on a mirrored bubble is refused (<see cref="FriendRefuses"/>). At the menu's close, every bubble left on
/// its speakers is cleared with the game's <c>SpeechBubbleController.Clear(anchor)</c>, and so is any line still started
/// there until <see cref="GuardQuietSeconds"/> pass with none (<see cref="FriendClosed"/>).</item>
/// </list>
/// <para>
/// Another view plugs in with the same calls: <see cref="HostBegin(AbstractMenu, int, SpeechBubbleController, SpeechBubbleAnchor, string)"/>
/// on the host and <see cref="FriendBegin(uint, int, SpeechBubbleController, SpeechBubbleAnchor, string, int)"/> on a
/// friend when a speaker's speech starts (each new speech: it resets the count), with the same slot on both; a menu's
/// speakers end with the menu. Speech outside any menu uses the context overloads (<c>HostBegin(string, ...)</c>,
/// <see cref="HostValue"/>, <see cref="HostEnd"/>; <c>FriendBegin(string, ...)</c>, <see cref="FriendHostValue"/>,
/// <see cref="FriendEnd"/>) and carries the value (0 to <see cref="SpeechProgress.MaxValue"/>) its own way.
/// </para>
/// Game thread only. Nothing here throws: a speaker whose bubble can't be read is dropped (logged), and the callers guard
/// every entry.
/// </summary>
internal static partial class SpeechMirror
{
    /// <summary>Canada curse speaker slots (the menu's own speaker ids are "pucklin" and "hatfreeze").</summary>
    public const int PucklinSlot = 0;

    /// <summary>The Canada curse's second speaker.</summary>
    public const int HatfreezeSlot = 1;

    /// <summary>After a close, the guard ends once no line has been seen for this long.</summary>
    public const double GuardQuietSeconds = 2;

    /// <summary>The longest a close's guard lasts.</summary>
    public const double GuardMaxSeconds = 10;

    // A speaker with no bubble up is read every this many frames (a line can't end without a press: one starting is caught
    // in time; a host's press reads its speaker at once).
    private const int IdleEveryFrames = 10;
    private const int MaxLoggedLines = 5;
    private const double ViewsEverySeconds = 0.5;

    private static readonly MenuTarget[] Targets = MakeTargets();
    private static readonly List<HostSpeaker> Hosts = new();
    private static readonly List<FriendSpeaker> Friends = new();
    private static readonly List<Guard> Guards = new();
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object>? _views;
    private static double _viewsAt;
    private static int _hostFrame;
    private static int _friendFrame;
    private static int _hostLogged;
    private static int _friendLogged;
    private static bool _driving;
    private static bool _refusalSaid;
    private static bool _fallbackSaid;

    /// <summary>True while the mod itself presses a bubble (its skip): the friend's refusal lets it through.</summary>
    public static bool Driving => _driving;

    /// <summary>True while any host speaker is mirrored (the press patch has work to do).</summary>
    public static bool HostSpeaking => Hosts.Count > 0;

    // ---------------------------------------------------------------- host

    /// <summary>
    /// Host: <paramref name="anchor"/> of <paramref name="controller"/>, in <paramref name="menu"/>, starts a speech as
    /// speaker <paramref name="slot"/> (0 to 7); <paramref name="who"/> names it for the log ("NPC \"Visit_01\"",
    /// "Pucklin"). A speaker already in that slot starts again (its summary logged): its count goes back to 0, what its
    /// anchor shows now isn't counted, and friends get value 0 (no line yet) before its first line.
    /// </summary>
    public static void HostBegin(AbstractMenu menu, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who)
    {
        if (menu != null)
        {
            Begin(menu.Pointer, string.Empty, MenuGame.Name(MenuGame.MenuId(menu), "menu"), slot, controller, anchor, who);
        }
    }

    /// <summary>
    /// Host: as <see cref="HostBegin(AbstractMenu, int, SpeechBubbleController, SpeechBubbleAnchor, string)"/> for speech in
    /// no menu, under a name of the caller's (<paramref name="context"/>); the caller sends <see cref="HostValue"/> its own way.
    /// </summary>
    public static void HostBegin(string context, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who)
    {
        if (!string.IsNullOrEmpty(context))
        {
            Begin(IntPtr.Zero, context, context, slot, controller, anchor, who);
        }
    }

    /// <summary>Host: the value of a context speaker for friends (0: no line yet, or no such speaker).</summary>
    public static int HostValue(string context, int slot) => FindHost(IntPtr.Zero, context, slot)?.Progress.Value ?? 0;

    /// <summary>Host: a context's speech is over; its speakers go (each with its summary).</summary>
    public static void HostEnd(string context) => EndHost(s => s.Menu == IntPtr.Zero && s.Context == context);

    /// <summary>Host: <paramref name="menu"/> is showing; a Canada curse menu's two speakers begin.</summary>
    public static void HostShowing(AbstractMenu menu)
    {
        if (menu.TryCast<CanadaCurseMenu>() is not { } curse)
        {
            return;
        }

        SpeechBubbleController? bubbles = curse.m_Metadata?.SpeechBubbles;
        if (bubbles == null || curse.m_PucklinAnchor == null || curse.m_HatfreezeAnchor == null)
        {
            ModLog.Warn("Menus: the host's Canada curse menu has no speech bubbles or speakers; friends' copies keep their own dialogue's first lines.");
            return;
        }

        HostBegin(menu, PucklinSlot, bubbles, curse.m_PucklinAnchor, "Pucklin");
        HostBegin(menu, HatfreezeSlot, bubbles, curse.m_HatfreezeAnchor, "Hatfreeze");
    }

    /// <summary>Host: <paramref name="menu"/> closed; its speakers go (each with its summary).</summary>
    public static void HostForget(IntPtr menu) => EndHost(s => s.Menu == menu);

    /// <summary>Host: hosting stopped; every speaker is forgotten.</summary>
    public static void ClearHost()
    {
        Hosts.Clear();
        _views = null;
        _hostLogged = 0;
    }

    /// <summary>Host: every speaker of <paramref name="menu"/> with its value now (for a menu just put on the board).</summary>
    public static void HostCurrent(IntPtr menu, Action<int, int> record)
    {
        foreach (HostSpeaker speaker in Hosts)
        {
            if (speaker.Menu == menu)
            {
                record(speaker.Slot, speaker.Progress.Value);
                speaker.Pushed = speaker.Progress.Value;
            }
        }
    }

    /// <summary>
    /// Host, every frame: each speaker's newest bubble read (every frame while one is up, else every
    /// <see cref="IdleEveryFrames"/>): a new line starts the next key; a menu speaker's changed value goes to
    /// <paramref name="record"/> (the menu, the slot, the value).
    /// </summary>
    public static void HostTick(Action<IntPtr, int, int> record)
    {
        _hostFrame++;
        for (int i = Hosts.Count - 1; i >= 0; i--)
        {
            HostSpeaker speaker = Hosts[i];
            if (_hostFrame >= speaker.NextRead && !ReadHost(speaker))
            {
                Hosts.RemoveAt(i);
                Summary(speaker);
                continue;
            }

            int value = speaker.Progress.Value;
            if (value != speaker.Pushed)
            {
                speaker.Pushed = value;
                if (speaker.Menu != IntPtr.Zero)
                {
                    record(speaker.Menu, speaker.Slot, value);
                }
            }
        }
    }

    /// <summary>
    /// Host: the game takes a press on <paramref name="view"/> (<c>SpeechBubbleView.RequestSkip</c>, before it acts): on a
    /// mirrored speaker's bubble, its line is read again now (a press in the frame a line starts counts on that line), then
    /// a typing line is shown in full, a waiting one advanced. Reads only.
    /// </summary>
    public static void HostPressed(SpeechBubbleView view)
    {
        SpeechBubbleAnchor? anchor = view.m_Anchor;
        HostSpeaker? speaker = anchor == null ? null : Hosts.Find(s => s.AnchorPointer == anchor.Pointer);
        if (speaker == null || !ReadHost(speaker))
        {
            return;
        }

        SpeechStage stage;
        switch (PhaseOf(view))
        {
            case SpeechPhase.Typing:
                stage = SpeechStage.Shown;
                break;
            case SpeechPhase.Waiting:
                stage = SpeechStage.Advanced;
                break;
            default:
                return; // between lines, or its press already came: the game does nothing with it either
        }

        if (speaker.Counter.Key == 0)
        {
            return;
        }

        SpeechProgress raised = speaker.Progress.Raised(speaker.Counter.Key, stage);
        if (raised == speaker.Progress)
        {
            return;
        }

        speaker.Progress = raised;
        speaker.Presses++;
        if (_hostLogged < MaxLoggedLines)
        {
            _hostLogged++;
            ModLog.Info($"Menus: the host's {speaker.MenuId} {speaker.Who}: {raised} (a press); friends follow." +
                (_hostLogged == MaxLoggedLines ? " Further presses are counted at its end." : string.Empty));
        }
    }

    // ---------------------------------------------------------------- friend

    /// <summary>Friend: following started (or stopped: then everything here goes, bubbles cleared).</summary>
    public static void FriendStart()
    {
        ClearFriend(gameClosing: false);
    }

    /// <summary>
    /// Friend: this PC's <paramref name="anchor"/> of <paramref name="controller"/> follows the host's speaker
    /// <paramref name="slot"/> of its menu <paramref name="serial"/>; <paramref name="who"/> names it ("NPC \"Visit_01\"");
    /// its first new line is <paramref name="firstKey"/> (more than 1 when it starts at the host's line). Call it before the
    /// speech starts here (what the anchor shows now isn't counted). A speaker already in that slot starts again.
    /// </summary>
    public static void FriendBegin(uint serial, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who, int firstKey = 1)
    {
        if (serial != 0)
        {
            Follow(serial, string.Empty, slot, controller, anchor, who, firstKey);
        }
    }

    /// <summary>Friend: as <see cref="FriendBegin(uint, int, SpeechBubbleController, SpeechBubbleAnchor, string, int)"/> for speech in no menu (the caller brings the host's value, <see cref="FriendHostValue"/>).</summary>
    public static void FriendBegin(string context, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who, int firstKey = 1)
    {
        if (!string.IsNullOrEmpty(context))
        {
            Follow(0, context, slot, controller, anchor, who, firstKey);
        }
    }

    /// <summary>
    /// Friend: the host's value for a context speaker (anything that isn't a progress reads as none). A value given before
    /// that speaker's <c>FriendBegin</c> is lost, so a caller gives the newest again once it's begun (every frame is fine).
    /// </summary>
    public static void FriendHostValue(string context, int slot, int value)
    {
        foreach (FriendSpeaker speaker in Friends)
        {
            if (speaker.Serial == 0 && speaker.Context == context && speaker.Slot == slot)
            {
                speaker.ContextValue = SpeechProgress.TryRead(value, out SpeechProgress progress) ? progress : SpeechProgress.None;
            }
        }
    }

    /// <summary>Friend: the lines a context speaker has started here so far (0: none, or no such speaker).</summary>
    public static int FriendLines(string context, int slot)
    {
        foreach (FriendSpeaker speaker in Friends)
        {
            if (speaker.Serial == 0 && speaker.Slot == slot && speaker.Context == context)
            {
                return speaker.Follow.OwnKey;
            }
        }

        return 0;
    }

    /// <summary>Friend: a context's speech is over here: its bubbles are cleared (and guarded), its speakers go.</summary>
    public static void FriendEnd(string context) => EndFriend(s => s.Serial == 0 && s.Context == context);

    /// <summary>
    /// Friend: one press of the mod's own on <paramref name="anchor"/>, the game's <c>Skip</c>, for a caller that runs a speech
    /// out itself (a followed bubble refuses any other press, <see cref="FriendRefuses"/>: a direct <c>Skip</c> on one does
    /// nothing). The caller guards it.
    /// </summary>
    public static void FriendPress(SpeechBubbleController controller, SpeechBubbleAnchor anchor)
    {
        _driving = true;
        try
        {
            controller.Skip(anchor);
        }
        finally
        {
            _driving = false;
        }
    }

    /// <summary>
    /// Friend: the host's speaker <paramref name="slot"/> of menu <paramref name="menu"/> as its value says it (none when it
    /// hasn't said, or says something that isn't a progress).
    /// </summary>
    public static SpeechProgress HostProgress(MirroredMenu menu, int slot) =>
        slot is >= 0 and < SpeechProgress.MaxSpeakers && menu.Values.TryGetValue(Targets[slot], out int value) && SpeechProgress.TryRead(value, out SpeechProgress progress)
            ? progress
            : SpeechProgress.None;

    /// <summary>Friend: this PC's menu <paramref name="menu"/> bound to the host's <paramref name="serial"/>: a Canada curse menu's two speakers follow the host's.</summary>
    public static void FriendBound(uint serial, AbstractMenu menu)
    {
        if (menu.TryCast<CanadaCurseMenu>() is not { } curse)
        {
            return;
        }

        SpeechBubbleController? bubbles = curse.m_Metadata?.SpeechBubbles;
        if (bubbles == null || curse.m_PucklinAnchor == null || curse.m_HatfreezeAnchor == null)
        {
            ModLog.Warn($"Menus: this PC's Canada curse menu has no speech bubbles or speakers; its dialogue can't follow {LogNames.HostTag()}'s.");
            return;
        }

        FriendBegin(serial, PucklinSlot, bubbles, curse.m_PucklinAnchor, "Pucklin");
        FriendBegin(serial, HatfreezeSlot, bubbles, curse.m_HatfreezeAnchor, "Hatfreeze");

        // Its cards come only after its own dialogue: once the host's are up, whatever still speaks here runs out (a line the
        // host's game never had, a press its patch couldn't see), so a friend's cards can't hang on a line.
        foreach (FriendSpeaker speaker in Friends)
        {
            if (speaker.Serial == serial)
            {
                speaker.EndsWithHostCards = true;
            }
        }

        LateWatch(serial, curse, bubbles);
    }

    /// <summary>
    /// Friend, every frame while following: each speaker follows the host's value (read every frame while it has work, else
    /// every <see cref="IdleEveryFrames"/>), and the closes' guards clear any line still started.
    /// </summary>
    public static void FriendTick(MenuMirror mirror)
    {
        _friendFrame++;
        double now = FrameClock.Seconds;
        for (int i = Friends.Count - 1; i >= 0; i--)
        {
            FriendSpeaker speaker = Friends[i];
            SpeechProgress host;
            bool hostOver = false;
            if (speaker.Serial == 0)
            {
                host = speaker.ContextValue;
            }
            else if (Mirrored(mirror, speaker.Serial) is { } menu)
            {
                speaker.MenuId = menu.State.Id;
                host = HostProgress(menu, speaker.Slot);
                hostOver = speaker.EndsWithHostCards && CanadaViews.HostCardsUp(menu);
            }
            else
            {
                // The host's menu is gone and nobody said: the close's clear and guard, as at a close.
                Friends.RemoveAt(i);
                End(speaker);
                continue;
            }

            bool changed = host != speaker.LastHost;
            speaker.LastHost = host;
            bool urgent = changed || speaker.LastSkipped || (!host.IsNone && host.Key > speaker.Follow.OwnKey);
            if (!urgent && _friendFrame < speaker.NextRead)
            {
                continue;
            }

            if (!Drive(speaker, host, hostOver, now))
            {
                Friends.RemoveAt(i);
                Summary(speaker);
            }
        }

        LateTick(mirror, now);
        for (int i = Guards.Count - 1; i >= 0; i--)
        {
            if (!Watch(Guards[i], now))
            {
                Guards.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Friend: this PC's view of the host's menu <paramref name="serial"/> closes: every bubble on its speakers is cleared
    /// (the game's own <c>SpeechBubbleController.Clear(anchor)</c>), then guarded a moment; each speaker's summary is logged.
    /// </summary>
    public static void FriendClosed(uint serial)
    {
        if (serial != 0)
        {
            EndFriend(s => s.Serial == serial);
            LateClosed(serial);
        }
    }

    /// <summary>Friend: following stopped. With <paramref name="gameClosing"/> nothing is touched, only forgotten.</summary>
    public static void ClearFriend(bool gameClosing)
    {
        LateClear(gameClosing);
        if (!gameClosing)
        {
            foreach (FriendSpeaker speaker in Friends)
            {
                Summary(speaker);
                TryClear(speaker.Controller, speaker.Anchor);
            }
        }

        Friends.Clear();
        Guards.Clear();
        _views = null;
        _friendLogged = 0;
        _refusalSaid = false;
    }

    /// <summary>
    /// Friend: the game takes a press on <paramref name="view"/> that isn't the mod's: true (refuse it) when it's a mirrored
    /// speaker's bubble (the host's presses drive it). Nothing local can press today (the pad and the UI maps are off on a
    /// friend): a backstop, said once.
    /// </summary>
    public static bool FriendRefuses(SpeechBubbleView view)
    {
        if (_driving || Friends.Count == 0)
        {
            return false;
        }

        SpeechBubbleAnchor? anchor = view.m_Anchor;
        if (anchor == null || !Friends.Exists(s => s.AnchorPointer == anchor.Pointer))
        {
            return false;
        }

        if (!_refusalSaid)
        {
            _refusalSaid = true;
            ModLog.Warn($"Menus: a press on a speech bubble that follows {LogNames.HostTag()}'s was refused on this PC (only {LogNames.HostTag()}'s presses move it; something local reached the bubble).");
        }

        return true;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The host's open menu <paramref name="serial"/> as mirrored here, or null (a plain loop: this runs every frame).</summary>
    private static MirroredMenu? Mirrored(MenuMirror mirror, uint serial)
    {
        IReadOnlyList<MirroredMenu> open = mirror.Open;
        for (int i = 0; i < open.Count; i++)
        {
            if (open[i].State.Serial == serial)
            {
                return open[i];
            }
        }

        return null;
    }

    private static MenuTarget[] MakeTargets()
    {
        var targets = new MenuTarget[SpeechProgress.MaxSpeakers];
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = MenuTarget.AtItem(SpeechProgress.Group, i);
        }

        return targets;
    }

    private static void Begin(IntPtr menu, string context, string menuId, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who)
    {
        if (slot is < 0 or >= SpeechProgress.MaxSpeakers || controller == null || anchor == null)
        {
            return;
        }

        HostSpeaker? speaker = FindHost(menu, context, slot);
        if (speaker != null)
        {
            Summary(speaker);
        }
        else
        {
            speaker = new HostSpeaker { Menu = menu, Context = context, Slot = slot };
            Hosts.Add(speaker);
        }

        speaker.Controller = controller;
        speaker.Anchor = anchor;
        speaker.AnchorPointer = anchor.Pointer;
        speaker.MenuId = menuId;
        speaker.Who = SafeText.Clean(who, 64);
        speaker.Progress = SpeechProgress.None;
        speaker.Pushed = -1; // value 0 goes out: friends drop what this slot said before
        speaker.Lines = 0;
        speaker.Presses = 0;
        speaker.NextRead = 0;
        Read(speaker, out long line, out SpeechPhase phase);
        speaker.Counter.Begin(line, phase);
    }

    private static HostSpeaker? FindHost(IntPtr menu, string context, int slot) =>
        Hosts.Find(s => s.Menu == menu && s.Context == context && s.Slot == slot);

    private static void EndHost(Predicate<HostSpeaker> which)
    {
        for (int i = Hosts.Count - 1; i >= 0; i--)
        {
            if (which(Hosts[i]))
            {
                Summary(Hosts[i]);
                Hosts.RemoveAt(i);
            }
        }
    }

    /// <summary>Host: the speaker's newest bubble read; a new line counted. False when its objects are gone.</summary>
    private static bool ReadHost(HostSpeaker speaker)
    {
        if (!Read(speaker, out long line, out SpeechPhase phase))
        {
            return false;
        }

        if (speaker.Counter.Observe(line, phase))
        {
            speaker.Lines++;
            speaker.Progress = speaker.Progress.Raised(speaker.Counter.Key, SpeechStage.Started);
        }

        speaker.NextRead = _hostFrame + (phase == SpeechPhase.None ? IdleEveryFrames : 1);
        return true;
    }

    private static void Follow(uint serial, string context, int slot, SpeechBubbleController controller, SpeechBubbleAnchor anchor, string who, int firstKey)
    {
        if (slot is < 0 or >= SpeechProgress.MaxSpeakers || controller == null || anchor == null)
        {
            return;
        }

        FriendSpeaker? speaker = Friends.Find(s => s.Serial == serial && s.Context == context && s.Slot == slot);
        if (speaker != null)
        {
            Summary(speaker);
        }
        else
        {
            speaker = new FriendSpeaker { Serial = serial, Context = context, Slot = slot };
            Friends.Add(speaker);
        }

        // A close's guard on this anchor ends: its menu shows again (reopened here) and speaks for itself.
        IntPtr pointer = anchor.Pointer;
        Guards.RemoveAll(g => g.AnchorPointer == pointer);
        speaker.Controller = controller;
        speaker.Anchor = anchor;
        speaker.AnchorPointer = pointer;
        speaker.Who = SafeText.Clean(who, 64);
        speaker.ContextValue = SpeechProgress.None;
        speaker.LastHost = SpeechProgress.None;
        speaker.LastSkipped = false;
        speaker.NextRead = 0;
        speaker.CatchUpSaid = false;
        speaker.ShownFirstSaid = false;
        speaker.EndsWithHostCards = false;
        speaker.RunOutSaid = false;
        speaker.MenuId = context.Length > 0 ? context : $"menu #{serial}"; // the menu's id from its first frame on

        Read(speaker, out long line, out SpeechPhase phase);
        speaker.Follow.Begin(line, phase, firstKey);
    }

    /// <summary>Friend: one frame of a speaker: its bubble read, the step decided and taken. False when its objects are gone.</summary>
    private static bool Drive(FriendSpeaker speaker, SpeechProgress host, bool hostOver, double now)
    {
        if (!Read(speaker, out long line, out SpeechPhase phase))
        {
            return false;
        }

        speaker.Follow.Observe(line, phase);
        SpeechFollowStep step = speaker.Follow.Next(host, now, hostOver);
        speaker.LastSkipped = step.Action == SpeechFollowAction.Skip;
        speaker.NextRead = _friendFrame + (phase == SpeechPhase.None ? IdleEveryFrames : 1);
        if (step.Action == SpeechFollowAction.Skip && !Skip(speaker))
        {
            return false;
        }

        Say(speaker, step, host);
        return true;
    }

    /// <summary>One press on this PC's bubble: the game's own skip of the speaker's anchor. False (logged) when it threw.</summary>
    private static bool Skip(FriendSpeaker speaker)
    {
        _driving = true;
        try
        {
            speaker.Controller.Skip(speaker.Anchor);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {speaker.MenuId}: pressing this PC's speech bubble of {LogNames.HostTag()}'s {speaker.Who} failed ({ModLog.SafeErrorText(ex)}); it no longer follows.");
            return false;
        }
        finally
        {
            _driving = false;
        }
    }

    private static void Say(FriendSpeaker speaker, SpeechFollowStep step, SpeechProgress host)
    {
        switch (step.Event)
        {
            case SpeechFollowEvent.Advanced or SpeechFollowEvent.ShownInFull when _friendLogged < MaxLoggedLines:
                _friendLogged++;
                string done = step.Event == SpeechFollowEvent.Advanced ? "advanced" : "shown in full";
                ModLog.Info($"Menus: {speaker.MenuId}: line {step.Key} of {LogNames.HostTag()}'s {speaker.Who} {done} here as on {LogNames.HostTag()}'s screen." +
                    (_friendLogged == MaxLoggedLines ? " Further lines are counted at its end." : string.Empty));
                break;
            case SpeechFollowEvent.ShownFirst when !speaker.ShownFirstSaid:
                speaker.ShownFirstSaid = true;
                ModLog.Info($"Menus: {speaker.MenuId}: line {step.Key} of {LogNames.HostTag()}'s {speaker.Who} was still typing here when {LogNames.HostTag()} moved on (a longer line in this PC's language); shown in full, then advanced.");
                break;
            case SpeechFollowEvent.CaughtUp when !speaker.CatchUpSaid:
                speaker.CatchUpSaid = true;
                ModLog.Info($"Menus: {speaker.MenuId}: {LogNames.HostTag()}'s {speaker.Who} is on line {host.Key} there, line {step.Key} here; catching up.");
                break;
            case SpeechFollowEvent.Stuck:
                ModLog.Warn($"Menus: {speaker.MenuId}: {LogNames.HostTag()}'s {speaker.Who} is stuck here at line {step.Key} ({speaker.Follow.Phase.ToString().ToLowerInvariant()}) after " +
                    string.Create(CultureInfo.InvariantCulture, $"{SpeechFollow.StuckSeconds:0} s of presses; pressing again every {SpeechFollow.StuckRetrySeconds:0.0} s."));
                break;
            case SpeechFollowEvent.Missing:
                ModLog.Warn($"Menus: {speaker.MenuId}: {LogNames.HostTag()}'s {speaker.Who} is on line {step.Key}, which this PC's speech doesn't have (it ended at line {speaker.Follow.OwnKey}); left out.");
                break;
            case SpeechFollowEvent.HostOver when !speaker.RunOutSaid:
                speaker.RunOutSaid = true;
                ModLog.Warn($"Menus: {speaker.MenuId}: {LogNames.HostTag()}'s cards are up there, but {LogNames.HostTag()}'s {speaker.Who} is still on line {step.Key} here " +
                    $"({(host.IsNone ? "nothing said" : host.ToString())} there); it runs out so this PC's cards come.");
                break;
        }
    }

    private static void EndFriend(Predicate<FriendSpeaker> which)
    {
        for (int i = Friends.Count - 1; i >= 0; i--)
        {
            FriendSpeaker speaker = Friends[i];
            if (which(speaker))
            {
                Friends.RemoveAt(i);
                End(speaker);
            }
        }
    }

    /// <summary>A friend's speaker ends: its bubble cleared if one is still up (logged), its summary, then its anchor guarded.</summary>
    private static void End(FriendSpeaker speaker)
    {
        if (Read(speaker, out _, out SpeechPhase phase))
        {
            if (phase != SpeechPhase.None && TryClear(speaker.Controller, speaker.Anchor))
            {
                ModLog.Info($"Menus: {speaker.MenuId} closed while {LogNames.HostTag()}'s {speaker.Who} was still speaking here (line {speaker.Follow.OwnKey} here, " +
                    $"{(speaker.LastHost.IsNone ? "none said" : $"line {speaker.LastHost.Key}")} on {LogNames.HostTag()}'s screen); its bubble is cleared.");
            }

            Guards.Add(new Guard(speaker.Controller, speaker.Anchor, speaker.MenuId, speaker.Who, FrameClock.Seconds));
        }

        Summary(speaker);
    }

    /// <summary>One frame of a close's guard: a line still started on the anchor is cleared. False once it's over.</summary>
    private static bool Watch(Guard guard, double now)
    {
        if (now - guard.Since > GuardMaxSeconds || now - guard.LastSeen > GuardQuietSeconds || guard.Controller == null || guard.Anchor == null)
        {
            if (guard.Cleared > 0)
            {
                ModLog.Info($"Menus: {guard.MenuId}: {guard.Cleared} more line(s) of {LogNames.HostTag()}'s {guard.Who} started here after the close; each was cleared.");
            }

            return false;
        }

        if (!ReadBubble(guard.Controller, guard.Anchor, fallback: false, out _, out SpeechPhase phase) || phase == SpeechPhase.None)
        {
            return true;
        }

        if (TryClear(guard.Controller, guard.Anchor))
        {
            guard.Cleared++;
            guard.LastSeen = now;
        }

        return true;
    }

    private static bool TryClear(SpeechBubbleController? controller, SpeechBubbleAnchor? anchor)
    {
        if (controller == null || anchor == null)
        {
            return false;
        }

        try
        {
            controller.Clear(anchor);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: clearing a speech bubble on this PC failed ({ModLog.SafeErrorText(ex)}).");
            return false;
        }
    }

    private static void Summary(HostSpeaker speaker)
    {
        if (speaker.Lines > 0)
        {
            ModLog.Info($"Menus: the host's {speaker.MenuId} {speaker.Who} spoke {speaker.Lines} line(s) ({speaker.Presses} press(es) mirrored for friends).");
        }
    }

    private static void Summary(FriendSpeaker speaker)
    {
        SpeechFollow follow = speaker.Follow;
        if (follow.Lines == 0 && follow.Presses == 0)
        {
            return;
        }

        string start = follow.FirstKey > 1 ? $"; started at line {follow.FirstKey}" : string.Empty;
        ModLog.Info($"Menus: {speaker.MenuId}: {LogNames.HostTag()}'s {speaker.Who} followed here: {follow.Lines} line(s), {follow.Presses} press(es) mirrored " +
            $"({follow.ShownFirst} shown in full first, {follow.CaughtUp} caught up){start}.");
        speaker.Follow.Begin(0, SpeechPhase.None); // said once
    }

    /// <summary>A speaker's newest bubble; false when its objects are gone. Switches the speaker to the fallback once if the list can't be read.</summary>
    private static bool Read(Speaker speaker, out long line, out SpeechPhase phase)
    {
        if (ReadBubble(speaker.Controller, speaker.Anchor, speaker.Fallback, out line, out phase))
        {
            return true;
        }

        line = 0;
        phase = SpeechPhase.None;
        if (speaker.Controller == null || speaker.Anchor == null || speaker.Fallback)
        {
            return false;
        }

        // The controller's list couldn't be read: the views on the anchor instead, from now on.
        speaker.Fallback = true;
        if (!_fallbackSaid)
        {
            _fallbackSaid = true;
            ModLog.Warn("Menus: this PC's speech bubbles couldn't be read through their controller's list; lines are followed by each bubble's phase instead.");
        }

        return ReadBubble(speaker.Controller, speaker.Anchor, fallback: true, out line, out phase);
    }

    /// <summary>
    /// The newest bubble of <paramref name="anchor"/>: its resolved line object (0: no bubble) and phase, from the controller's
    /// <c>m_Active</c>; with <paramref name="fallback"/>, from the active bubble views on the anchor (the line object is then
    /// <see cref="SpeechLineCounter.Unknown"/>). False when the objects are gone or the list can't be read.
    /// </summary>
    private static bool ReadBubble(SpeechBubbleController? controller, SpeechBubbleAnchor? anchor, bool fallback, out long line, out SpeechPhase phase)
    {
        line = 0;
        phase = SpeechPhase.None;
        if (controller == null || anchor == null)
        {
            return false;
        }

        try
        {
            if (fallback)
            {
                SpeechBubbleView? found = FindView(anchor);
                line = found == null ? 0 : SpeechLineCounter.Unknown;
                phase = found == null ? SpeechPhase.None : PhaseOf(found);
                return true;
            }

            Il2CppSystem.Collections.Generic.Dictionary<SpeechBubbleAnchor, Il2CppSystem.Collections.Generic.List<SpeechBubbleController.ActiveBubble>>? active = controller.m_Active;
            if (active == null || !active.ContainsKey(anchor))
            {
                return active != null;
            }

            Il2CppSystem.Collections.Generic.List<SpeechBubbleController.ActiveBubble>? bubbles = active[anchor];
            int count = bubbles?.Count ?? 0;
            SpeechBubbleController.ActiveBubble? newest = count > 0 ? bubbles![count - 1] : null;
            SpeechBubbleView? view = newest?.View;
            if (newest == null || view == null)
            {
                return true;
            }

            SpeechLineData? resolved = newest.Line;
            line = resolved == null ? 0 : resolved.Pointer.ToInt64();
            phase = PhaseOf(view);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: reading a speech bubble {(fallback ? "among the bubble views" : "through its controller")} failed ({ModLog.SafeErrorText(ex)}).");
            return false;
        }
    }

    /// <summary>
    /// The fallback: the active bubble view bound to <paramref name="anchor"/>, among every one loaded (only after the list
    /// failed; the search walks every loaded object, so its answer is kept <see cref="ViewsEverySeconds"/>: the game pools its
    /// bubble views).
    /// </summary>
    private static SpeechBubbleView? FindView(SpeechBubbleAnchor anchor)
    {
        double now = FrameClock.Seconds;
        if (_views == null || now - _viewsAt > ViewsEverySeconds)
        {
            _views = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpeechBubbleView>());
            _viewsAt = now;
        }

        foreach (UnityEngine.Object any in _views)
        {
            SpeechBubbleView? view = any?.TryCast<SpeechBubbleView>();
            if (view != null && view.m_Active && view.m_Anchor != null && view.m_Anchor.Pointer == anchor.Pointer)
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>
    /// A bubble's phase as far as a press goes. The game only ever writes Typing and Waiting (verified in the game's code):
    /// a bubble keeps its Waiting and its signalled <c>m_AdvanceTcs</c> after its press, through the delay to its next
    /// line and into that line's own opening, and a pooled bubble into its next use, while <c>RequestSkip</c> does nothing there
    /// (<c>TrySignalCompletion</c> fails, verified in the game's code). So Waiting means a press still to come.
    /// </summary>
    private static SpeechPhase PhaseOf(SpeechBubbleView view) => view.m_Phase switch
    {
        SpeechBubbleView.Phase.Typing => SpeechPhase.Typing,
        SpeechBubbleView.Phase.Waiting when view.m_AdvanceTcs is { } advance && advance.UnsafeGetStatus() == Il2CppCysharp.Threading.Tasks.UniTaskStatus.Pending => SpeechPhase.Waiting,
        _ => SpeechPhase.Idle,
    };

    /// <summary>A mirrored speaker on either side: a speech controller's anchor in a slot.</summary>
    private abstract class Speaker
    {
        public SpeechBubbleController Controller { get; set; } = null!;

        public SpeechBubbleAnchor Anchor { get; set; } = null!;

        public IntPtr AnchorPointer { get; set; }

        public int Slot { get; init; }

        public string Context { get; init; } = string.Empty;

        public string Who { get; set; } = string.Empty;

        public string MenuId { get; set; } = string.Empty;

        /// <summary>The controller's list couldn't be read: the views on the anchor are.</summary>
        public bool Fallback { get; set; }

        /// <summary>The frame of the next read when there's nothing urgent.</summary>
        public int NextRead { get; set; }
    }

    /// <summary>The host's speaker: its line count and progress.</summary>
    private sealed class HostSpeaker : Speaker
    {
        /// <summary>The host's menu (zero for a context speaker).</summary>
        public IntPtr Menu { get; init; }

        public SpeechLineCounter Counter { get; } = new();

        public SpeechProgress Progress { get; set; }

        /// <summary>The value last given to the board (-1: none yet).</summary>
        public int Pushed { get; set; } = -1;

        public int Lines { get; set; }

        public int Presses { get; set; }
    }

    /// <summary>A friend's speaker: the host's menu it follows, and how it follows.</summary>
    private sealed class FriendSpeaker : Speaker
    {
        /// <summary>The host's menu serial (0 for a context speaker).</summary>
        public uint Serial { get; init; }

        public SpeechFollow Follow { get; } = new();

        /// <summary>A context speaker's host value, as the caller last gave it.</summary>
        public SpeechProgress ContextValue { get; set; }

        public SpeechProgress LastHost { get; set; }

        public bool LastSkipped { get; set; }

        public bool CatchUpSaid { get; set; }

        public bool ShownFirstSaid { get; set; }

        /// <summary>Its speech ends with the host's cards at the latest (the Canada curse's: <see cref="CanadaViews.HostCardsUp"/>).</summary>
        public bool EndsWithHostCards { get; set; }

        public bool RunOutSaid { get; set; }
    }

    /// <summary>A closed speaker's anchor, watched a moment for lines its menu's own dialogue still starts.</summary>
    private sealed class Guard
    {
        public Guard(SpeechBubbleController controller, SpeechBubbleAnchor anchor, string menuId, string who, double since)
        {
            Controller = controller;
            Anchor = anchor;
            AnchorPointer = anchor.Pointer;
            MenuId = menuId;
            Who = who;
            Since = since;
            LastSeen = since;
        }

        public SpeechBubbleController Controller { get; }

        public SpeechBubbleAnchor Anchor { get; }

        public IntPtr AnchorPointer { get; }

        public string MenuId { get; }

        public string Who { get; }

        public double Since { get; }

        public double LastSeen { get; set; }

        public int Cleared { get; set; }
    }
}
#endif
