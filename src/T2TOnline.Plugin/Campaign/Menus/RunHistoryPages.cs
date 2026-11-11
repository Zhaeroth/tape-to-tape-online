#if HAS_INTEROP
using System;
using Il2CppState.RunHistory;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Progress;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;
using T2TOnline.Plugin.Streaming;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's run history at the title (the run-end menu in its history mode) as a real view on a friend.
/// The menu shows one run at a time from the entry it holds, with page arrows from its place in the list; its page step loads
/// the next entry from this PC's files and shows it with four calls (<c>ClearSkaters</c>, <c>ShowHistoryEntry</c>,
/// <c>RefreshDisplay</c>, <c>MatchGoalieRowNow</c>).
/// <list type="bullet">
/// <item>Host: each page the menu shows (<c>ShowHistoryEntry</c>'s postfix) goes out as a
/// <see cref="MenuAttachmentKind.HistoryPage"/> attachment: its place, the list's length, the host's UTC offset at the run's
/// time and the run (<see cref="HistoryPageData"/>).</item>
/// <item>Friend: the menu opens in its history mode on the host's page (a list of the host's length, so its arrows show as
/// the host's); each newer page goes in with the game's own four calls after the entry and place are set, as its page step
/// does after its load (this PC's own history is never read: its step stays refused). The date shows the host's local
/// time (<see cref="HistoryPageData.LocalTicks"/>).</item>
/// <item>A page from a custom-logo run names <c>custom_&lt;file&gt;.png</c>, a sprite only the host's PC has (v1).
/// The host sends the logo of the page it rests on through the custom content carrier (<see cref="HostTick"/>: one
/// logo a page, each once a session, within <see cref="HistoryLogoShare"/>'s budget); the friend's game puts it in its logo
/// list as it does a match's (<see cref="CustomContent"/>) and shows the page again with it (FriendMenus.Title.cs). A logo
/// that can't go leaves the friend's default logo, and the log says why.</item>
/// </list>
/// </summary>
internal static class RunHistoryPages
{
    private const int LinesPerMinute = 20;
    private const double ShareEverySeconds = 1;

    private static readonly LineBudget Lines = new("Menus (run history)", LinesPerMinute);

    // Host: the logo of the page up now goes to friends (HistoryLogoShare decides which, when and how many).
    private static readonly HistoryLogoShare Share = new();
    private static CustomItem? _logoItem;
    private static RunEndMenu? _menu;
    private static double _nextShare;

    /// <summary>Set by the online session: sends a page's logo to every friend who hasn't had it (<c>MatchRelay.ShareHistoryLogo</c>).</summary>
    public static Action<CustomItem>? ShareLogo { get; set; }

    // ---- Host ----

    /// <summary>
    /// Host: the history mode showed a page (<c>ShowHistoryEntry</c>'s postfix): it goes to friends as the menu's newest
    /// page (now, or once the menu is on the board). A page that can't be sent is logged; friends keep the last one, or a
    /// note.
    /// </summary>
    public static void HostShown(HostMenus host, RunEndMenu menu, bool friends)
    {
        RunHistoryEntryModel? entry = menu.m_HistoryEntry;
        if (!menu.m_IsHistoryMode || entry == null)
        {
            return;
        }

        RunHistoryData run = RunHistoryMapping.ToData(entry);
        NotePageLogo(menu, entry, run);
        var page = new HistoryPageData
        {
            Index = menu.m_HistoryIndex,
            Count = menu.m_HistoryRuns?.Count ?? 0,
            HostUtcOffsetMinutes = OffsetMinutes(run.TimestampUtc),
            Entry = run,
        };
        string? problem = page.Problem() ?? (run.IsWithinLimits() ? null : "the run is over the run history entry's limits");
        if (problem == null && !host.Attach(menu, MenuAttachmentKind.HistoryPage, serial => MenuAttachment.HistoryPage(serial, page), page.ToString(), friends, out string made))
        {
            problem = made;
        }

        if (problem != null)
        {
            ModLog.Warn($"Menus: the host's run history {page} can't go to friends ({problem}).");
            return;
        }

        Log($"Menus: the host's run history {page} goes to friends.");
    }

    /// <summary>
    /// Host: the page up wears <paramref name="entry"/>'s team logo. A custom logo is read and queued for friends once the page
    /// has been up a second (<see cref="HostTick"/>); a name the page can't carry whole (cut short by the run history's limit)
    /// isn't sent: a friend's page would wait for a sprite of another name.
    /// </summary>
    private static void NotePageLogo(RunEndMenu menu, RunHistoryEntryModel entry, RunHistoryData run)
    {
        string? logo = entry.teamSnapshot?.logoName;
        if (HistoryLogoShare.IsCustom(logo) && !string.Equals(run.Team?.LogoName, logo, StringComparison.Ordinal))
        {
            // The logo's name is its file's, which its player chose: the log has its shape (D127), as below.
            ModLog.Warn($"Menus: the host's run history page wears a custom logo ({LogNames.TextShape(logo)}) whose name is too long for the page to carry; their page shows the game's default logo.");
            logo = null;
        }

        Share.PageShown(logo, FrameClock.NowMicros);
        _menu = menu;
    }

    /// <summary>
    /// Host, every frame of the menu views (once a second inside): the logo of the history page the host rests on goes to
    /// friends through the custom content carrier (<see cref="HistoryLogoShare"/>: read from the game's custom logo folder once
    /// a page has been up a second, queued once for each friend, within the session's budget); a friend who joined meanwhile
    /// gets it with the next call. Nothing once the history menu is gone. Never throws.
    /// </summary>
    public static void HostTick(bool friends)
    {
        if (!friends || ShareLogo == null || _menu == null)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _nextShare)
        {
            return;
        }

        _nextShare = now + ShareEverySeconds;
        try
        {
            if (PageGone())
            {
                Share.PageGone();
                _logoItem = null;
                _menu = null;
                return;
            }

            if (Share.ShouldRead(FrameClock.NowMicros, out string name))
            {
                ReadPageLogo(name);
            }

            if (_logoItem != null && string.Equals(_logoItem.Key, Share.Page, StringComparison.Ordinal))
            {
                ShareLogo(_logoItem);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: sending the host's run history page's logo to friends failed ({ModLog.SafeErrorText(ex)}); their page keeps the game's default logo.");
            _logoItem = null;
            Share.PageGone();
        }
    }

    /// <summary>Host: hosting stopped.</summary>
    public static void ClearHost()
    {
        Share.Clear();
        _logoItem = null;
        _menu = null;
    }

    /// <summary>The page's logo read from the host's custom logo folder and admitted within the session's budget, or why it can't go (said once).</summary>
    private static void ReadPageLogo(string name)
    {
        CustomItem? item = CustomContent.HistoryLogoItem(name, out string problem);
        if (item == null)
        {
            Share.Unreadable(name);
            _logoItem = null;
            ModLog.Warn($"Menus: the host's run history page wears a custom logo ({LogNames.TextShape(name)}), which can't go to friends ({problem}); their page shows the game's default logo.");
            return;
        }

        long pixels = PngHeader.TryRead(item.Bytes, out int width, out int height) ? (long)width * height : 0;
        HistoryLogoVerdict verdict = Share.Admit(name, item.Bytes.Length, pixels);
        if (!verdict.Sent)
        {
            _logoItem = null;
            ModLog.Warn($"Menus: the host's run history page wears a custom logo ({LogNames.TextShape(name)}), which doesn't go to friends: {verdict.Why}; their page shows the game's default logo.");
            return;
        }

        _logoItem = item;
        ModLog.Info($"Menus: the host's run history page wears a custom logo ({LogNames.TextShape(name)}, {item.Bytes.Length / 1024} KB); it goes to friends now, once a session " +
            $"({Share.Logos} of {HistoryLogoShare.MaxLogos} history page logos this session, {Share.Bytes / 1024} KB of {HistoryLogoShare.MaxBytes / (1024 * 1024)} MB).");
    }

    /// <summary>True when the history menu the page was shown in is gone, hidden or no longer in its history mode.</summary>
    private static bool PageGone()
    {
        try
        {
            return _menu == null || _menu.WasCollected || _menu.IsHidden || !_menu.m_IsHistoryMode;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The host's UTC offset at a run's UTC time (its game shows that time as its local time), in minutes.</summary>
    private static int OffsetMinutes(long utcTicks)
    {
        long ticks = Math.Clamp(utcTicks, 0, DateTime.MaxValue.Ticks);
        double minutes = TimeZoneInfo.Local.GetUtcOffset(new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes;
        return (int)Math.Clamp(Math.Round(minutes), -HistoryPageData.MaxOffsetMinutes, HistoryPageData.MaxOffsetMinutes);
    }

    // ---- Friend ----

    /// <summary>The host's page in <paramref name="attachment"/>, or null with why.</summary>
    public static HistoryPageData? Page(MenuAttachment? attachment, out string problem)
    {
        problem = string.Empty;
        if (attachment == null || !attachment.TryGetHistoryPage(out HistoryPageData? page) || page?.Entry == null)
        {
            problem = "the host's run history page isn't here";
            return null;
        }

        if (page.Entry.Team == null || page.Entry.Team.Forwards.Count == 0)
        {
            problem = "the host's run history page has no team";
            return null;
        }

        return page;
    }

    /// <summary>
    /// Friend: the data to open the host's history at <paramref name="page"/>: the menu's history mode with a list of the
    /// host's length (only its page's place holds the run; the others are never read, its page step being refused here),
    /// the page's run and place. Null with why when the run can't be made into the game's entry.
    /// </summary>
    public static RunEndMenu.Metadata? Metadata(HistoryPageData page, int minSkaters, out string problem)
    {
        RunHistoryEntryModel? entry = Entry(page, out problem);
        if (entry == null)
        {
            return null;
        }

        var runs = new Il2CppList.List<RunHistoryIndexEntry>(page.Count);
        for (int i = 0; i < page.Count; i++)
        {
            runs.Add(i == page.Index
                ? new RunHistoryIndexEntry { runID = entry.runID, timestampUtc = entry.timestampUtc, slot = -1 }
                : new RunHistoryIndexEntry { runID = string.Empty, slot = -1 });
        }

        return new RunEndMenu.Metadata
        {
            FillWithRandomData = false,
            IsDuringRun = false,
            HistoryRuns = runs,
            HistoryEntry = entry,
            StartIndex = page.Index,
            MinNumSkaters = minSkaters,
            PlayLoadingTransition = false,
        };
    }

    /// <summary>
    /// Friend: the open menu moves to <paramref name="page"/> as its own page step does after its load: the entry and place
    /// set (and the list's length, if the host's changed), then <c>ClearSkaters</c>, <c>ShowHistoryEntry</c>,
    /// <c>RefreshDisplay</c>, <c>MatchGoalieRowNow</c>. False, with why, when the run can't be made into the game's entry.
    /// Exceptions from the game's calls reach the caller (it runs them guarded).
    /// </summary>
    public static bool Show(RunEndMenu menu, HistoryPageData page, out string problem)
    {
        RunHistoryEntryModel? entry = Entry(page, out problem);
        if (entry == null)
        {
            return false;
        }

        Il2CppList.List<RunHistoryIndexEntry>? runs = menu.m_HistoryRuns;
        if (runs == null || runs.Count != page.Count)
        {
            runs = new Il2CppList.List<RunHistoryIndexEntry>(page.Count);
            for (int i = 0; i < page.Count; i++)
            {
                runs.Add(new RunHistoryIndexEntry { runID = string.Empty, slot = -1 });
            }

            menu.m_HistoryRuns = runs;
        }

        runs[page.Index] = new RunHistoryIndexEntry { runID = entry.runID, timestampUtc = entry.timestampUtc, slot = -1 };
        menu.m_HistoryIndex = page.Index;
        menu.m_HistoryEntry = entry;
        menu.ClearSkaters();
        menu.ShowHistoryEntry();
        menu.RefreshDisplay();
        menu.MatchGoalieRowNow();
        return true;
    }

    /// <summary>One line for the log, up to <see cref="LinesPerMinute"/> a minute (the host may page quickly).</summary>
    public static void Log(string line)
    {
        if (Lines.TrySpend())
        {
            ModLog.Info(line);
        }
    }

    /// <summary>The page's run as the game's entry, its time moved so this PC's local time shows the host's; null with why.</summary>
    private static RunHistoryEntryModel? Entry(HistoryPageData page, out string problem)
    {
        problem = string.Empty;
        try
        {
            RunHistoryEntryModel entry = RunHistoryMapping.ToGame(page.Entry!);
            entry.timestampUtc = HistoryPageData.LocalTicks(page.Entry!.TimestampUtc, page.HostUtcOffsetMinutes, at => TimeZoneInfo.Local.GetUtcOffset(at));
            return entry;
        }
        catch (Exception ex)
        {
            problem = $"the host's run couldn't be made into the game's entry ({ModLog.SafeErrorText(ex)})";
            return null;
        }
    }
}
#endif
