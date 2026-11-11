namespace T2TOnline.Core.Campaign;

/// <summary>
/// Run sync's save safety (D41, D46): what the game's run file is (the solo run, a co-op slot,
/// or on a friend the host's run), whether the run in the game's memory is that file's, and from both whether the game may
/// save or delete its run now. Game-independent: run sync tells it what happened (a session started or ended, a load of
/// the host's run started, was given up on or finished, the run file switched, a scene loaded, what the title screen is
/// doing) and acts on what it answers. Times are seconds on any clock that only goes forward. Not thread-safe: the game
/// thread only.
/// </summary>
/// <remarks>
/// The rule it keeps: a run save, or the run's end (which deletes the run file), goes through only when the run in memory
/// is the run file's. On a friend that's never while following or holding the host's run; after the host's run was put
/// away, after a switch of the run file, or after a given-up load of the host's run finished, only once the title screen
/// has reloaded the run file's run (or a run has started from it with that run), and with
/// <see cref="StaleUntilTitle"/> only after the title screen has loaded first.
/// </remarks>
public sealed class RunSaveSafety
{
    /// <summary>The game's run file is the solo run's (<c>RunDataV2.es3</c>).</summary>
    public const int SoloSlot = 0;

    /// <summary>The game's run file is the mod's copy of the host's run (a friend's game following a campaign).</summary>
    public const int HostRunSlot = -1;

    /// <summary>How long after the run was marked stale the title screen must have been idle before it counts as reloaded.</summary>
    public const double StaleFloorSeconds = 1;

    /// <summary>Why the run in memory is held after a session started with this game's own run in play away from the title.</summary>
    public const string OwnRunInPlay =
        "this game's own run was in play away from the title screen when the session started, so what the session left of it in memory (a skipped match end, say) isn't saved";

    /// <summary>Why the run in memory is held after a run was started before the title screen reloaded the switched run file.</summary>
    public const string StartedBeforeReload =
        "a run was started from the title screen while it was still loading the run from before the run file switched, so the run in memory may be that one";

    /// <summary>True while this PC hosts a session.</summary>
    public bool Hosting { get; private set; }

    /// <summary>The co-op slot a host's campaign uses (1-3).</summary>
    public int HostSlot { get; private set; }

    /// <summary>True while this PC follows a host's campaign (a friend in a session).</summary>
    public bool Following { get; private set; }

    /// <summary>True while the save guard holds for this friend's session.</summary>
    public bool GuardHolds { get; private set; }

    /// <summary>True while the save guard holds after a session that ended on the host's campaign, until the title screen loads.</summary>
    public bool GuardUntilTitle { get; private set; }

    /// <summary>What the game's run file is: <see cref="SoloSlot"/>, a co-op slot (1-3), or <see cref="HostRunSlot"/>.</summary>
    public int ActiveSlot { get; private set; }

    /// <summary>True while the game's campaign state holds the host's run (its run repositories point at the mod's file).</summary>
    public bool HoldingHostRun { get; private set; }

    /// <summary>True while a load of the host's run that was given up on still runs (it ends by putting its run in memory).</summary>
    public bool OrphanRunning { get; private set; }

    /// <summary>Why the run in memory may not be the run file's, or null when it is.</summary>
    public string? StaleRun { get; private set; }

    /// <summary>With <see cref="StaleRun"/>: the title screen has to load before the run can be the run file's again.</summary>
    public bool StaleUntilTitle { get; private set; }

    /// <summary>When the run was last marked stale.</summary>
    public double StaleSince { get; private set; }

    /// <summary>True while run sync should refresh the title screen's campaign data (which reloads the run file's run) once it's idle.</summary>
    public bool RefreshWanted { get; private set; }

    /// <summary>
    /// True when the run file switched while the title screen was loading a run (from the file before): until run sync's
    /// own refresh of the title has started, a run started from the title may be that one, not the run file's.
    /// </summary>
    public bool LoadBeforeSwitch { get; private set; }

    /// <summary>Host: true while hosting with the chosen co-op slot as the game's run file, i.e. the campaign is shared.</summary>
    public bool HostOnCoopSlot => Hosting && HostSlot > 0 && ActiveSlot == HostSlot;

    /// <summary>What the game's run file should be: a friend's while following stays as it is; a host's is the co-op slot; else the solo run.</summary>
    public int WantedSlot => Following ? ActiveSlot : Hosting ? HostSlot : SoloSlot;

    /// <summary>True while this PC follows a host or holds the host's run: its skipped run saves are routine, not worth a warning.</summary>
    public bool InFriendSession => Following || HoldingHostRun;

    /// <summary>True when a host's checkpoint may be loaded here: following, with the save guard holding.</summary>
    public bool MayLoadHostRun => Following && GuardHolds;

    /// <summary>
    /// Why this PC's run saves and the run's end (which deletes the run file) are skipped now, or null when they go through:
    /// while following or holding the host's run, while a load of the host's run may still finish, and while the run in
    /// memory may not be the run file's.
    /// </summary>
    public string? RunSaveBlock =>
        Following || HoldingHostRun ? "a friend's run file is never written"
        : OrphanRunning ? "a load of the host's run is still running here, so the run in memory may become the host's; your run file is left alone"
        : StaleRun == null ? null
        : StaleUntilTitle ? $"{StaleRun}; your run file is left alone until this game's title screen has loaded and reloaded your run"
        : $"{StaleRun}; your run file is left alone until the title screen has reloaded the run";

    /// <summary>For a check that failed: true when the run in memory may not be the run file's (so, in doubt, it isn't written).</summary>
    public bool MayHoldAnotherRun => Following || HoldingHostRun || OrphanRunning || StaleRun != null;

    /// <summary>A session as host starts, on co-op slot <paramref name="slot"/>.</summary>
    public void StartHosting(int slot)
    {
        Hosting = true;
        HostSlot = slot;
    }

    /// <summary>The host's session ended. The co-op slot stays the run file until the title screen switches it back.</summary>
    public void StopHosting() => Hosting = false;

    /// <summary>The chosen co-op slot changed.</summary>
    public void CoopSlotChosen(int slot) => HostSlot = slot;

    /// <summary>
    /// True when the co-op slot can't change now (<paramref name="idleTitle"/>: the title screen is up with nothing happening
    /// on it), with why, worded to follow "Slot N wasn't selected: ".
    /// </summary>
    public bool CoopSlotLocked(bool idleTitle, out string problem)
    {
        if ((Hosting || ActiveSlot > 0) && !idleTitle)
        {
            problem = Hosting
                ? "you're hosting, so only at the title screen with nothing open on it"
                : $"a co-op run in slot {ActiveSlot} is still being played; only at the title screen";
            return true;
        }

        problem = string.Empty;
        return false;
    }

    /// <summary>A friend's session starts; <paramref name="guardHolds"/>: the save guard holds for it. It takes over from the last session's hold until the title.</summary>
    public void StartFollowing(bool guardHolds)
    {
        Following = true;
        GuardUntilTitle = false;
        GuardHolds = guardHolds;
    }

    /// <summary>Whether the save guard holds for this friend's session (after <see cref="StartFollowing"/>, once it was asked).</summary>
    public void GuardHeld(bool holds) => GuardHolds = holds && Following;

    /// <summary>
    /// A friend's session started (after <see cref="StartFollowing"/>) with this game's own campaign run in play away from
    /// the title screen (a join is refused there, <see cref="JoinRefusal"/>, so this is the race of a
    /// join with a Continue). The session replaces that run in memory or leaves it part-way (its match's end is the
    /// host's), so after the session run saves stay held until the title screen has loaded and reloaded the run file's run:
    /// the run file stays as it was, and the run that comes back is the one in it.
    /// </summary>
    public void OwnRunLeftInPlay(double now) => MarkStale(OwnRunInPlay, untilTitle: true, now);

    /// <summary>
    /// Why a join must wait (on screen), or null when it may go ahead: while this player's own campaign run is in play
    /// (<paramref name="place"/>), what it did since its last save point is only in memory, and a session would replace it. The way to the
    /// title screen saves it (the world map's own way back saves the run).
    /// </summary>
    public static string? JoinRefusal(OwnRunPlace place) => place switch
    {
        OwnRunPlace.Map => "You're in your own campaign run: go back to the title screen first (the game saves your run on the way), then join.",
        OwnRunPlace.Match => "You're in a match of your own campaign run: finish it, go back to the title screen (the game saves your run on the way), then join.",
        _ => null,
    };

    /// <summary>
    /// A friend's session ended (after <see cref="PutHostRunAway"/>). True when the save guard should hold on until this game's
    /// title screen loads: it held for the session, the game isn't closing, and the host's campaign is still on screen
    /// (<see cref="StaleUntilTitle"/>), where closing a world-map menu or quitting the map saves or deletes the run. Then
    /// tell it whether the guard did (<see cref="GuardKeptUntilTitle"/>).
    /// </summary>
    public bool StopFollowing(bool gameClosing)
    {
        bool held = GuardHolds;
        Following = false;
        GuardHolds = false;
        return held && !gameClosing && StaleUntilTitle;
    }

    /// <summary>Whether the save guard holds on until the title screen loads (after <see cref="StopFollowing"/> asked for it).</summary>
    public void GuardKeptUntilTitle(bool kept) => GuardUntilTitle = kept;

    /// <summary>The run file now points at the host's run, and the game's loader is about to load it.</summary>
    public void HostRunPointed()
    {
        HoldingHostRun = true;
        ActiveSlot = HostRunSlot;
    }

    /// <summary>A load of the host's run was given up on while it still runs (timed out, or the session ended).</summary>
    public void LoadOrphaned() => OrphanRunning = true;

    /// <summary>
    /// The given-up load finished, so the game's memory may now hold the host's run. Outside a session, with the game's campaign
    /// there (<paramref name="campaignThere"/>): on a quiet title screen (<paramref name="quietTitle"/>: up, no run starting,
    /// not leaving) the campaign state is to be reset and the title reloads the run file's run; anywhere else run saves are
    /// held until the title screen is back.
    /// </summary>
    public OrphanLanded OrphanFinished(bool campaignThere, bool quietTitle, double now)
    {
        OrphanRunning = false;
        if (Following || HoldingHostRun || !campaignThere)
        {
            return OrphanLanded.Nothing;
        }

        if (quietTitle)
        {
            MarkStale("a load of the host's run finished after the session ended", untilTitle: false, now);
            return OrphanLanded.ResetAtTitle;
        }

        MarkStale("a load of the host's run finished after the session ended, so the run in memory may be the host's", untilTitle: true, now);
        return OrphanLanded.HeldUntilTitle;
    }

    /// <summary>
    /// The host's run is put away (the session ended, or the host went to its title screen). False when it never was here
    /// (nothing to put back). Else run saves are held until the title screen has reloaded this game's own run, and, unless
    /// the game is on a settled title screen (<paramref name="atSettledTitle"/>: loaded, not leaving), until it has loaded
    /// first: a world map landing after this would start a new run on the reset state and save it.
    /// </summary>
    public bool PutHostRunAway(bool atSettledTitle, double now)
    {
        bool held = HoldingHostRun || ActiveSlot == HostRunSlot;
        HoldingHostRun = false;
        if (!held)
        {
            return false;
        }

        MarkStale("the host's run was just put away and this game's own run isn't back in memory yet", untilTitle: !atSettledTitle, now);
        return true;
    }

    /// <summary>
    /// The run file points at the solo run again after the host's run was put away. <paramref name="titleLoading"/>: the title
    /// screen was loading a run (the host's) at that moment.
    /// </summary>
    public void PointedBackToSolo(bool titleLoading)
    {
        ActiveSlot = SoloSlot;
        LoadBeforeSwitch |= titleLoading;
    }

    /// <summary>
    /// The run file switched to <paramref name="slot"/> (the solo run or a co-op slot); the run in memory is still the one
    /// before. <paramref name="titleLoading"/>: the title screen was loading a run (from the file before) at that moment.
    /// </summary>
    public void Switched(int slot, bool titleLoading, double now)
    {
        ActiveSlot = slot;
        MarkStale($"the run file just became {(slot > 0 ? $"co-op slot {slot}" : "your solo run")} and the run in memory is still the one before", untilTitle: false, now);
        LoadBeforeSwitch |= titleLoading;
    }

    /// <summary>True when the run file may switch now (besides the title screen being idle): no load of the host's run runs here.</summary>
    public bool MaySwitchRunFile(bool hostLoadRunning) => !OrphanRunning && !hostLoadRunning;

    /// <summary>Run sync refreshed the title screen's campaign data: the run file's run is being loaded.</summary>
    public void TitleRefreshed()
    {
        RefreshWanted = false;
        LoadBeforeSwitch = false;
    }

    /// <summary>
    /// The title screen's scene loaded (its own refresh loads the run file as it is now). True when the save guard's hold
    /// until the title (after a session that ended on the host's campaign) is over and nothing else of run sync's holds it:
    /// release it.
    /// </summary>
    public bool TitleSceneLoaded()
    {
        StaleUntilTitle = false;
        LoadBeforeSwitch = false;
        if (!GuardUntilTitle)
        {
            return false;
        }

        GuardUntilTitle = false;
        return !Following;
    }

    /// <summary>
    /// A scene other than the title screen loaded (<paramref name="scene"/>): the game left its title screen for a run, and on
    /// the way loaded (Continue) or made (New Run) the run of its run file, so that's the run in memory now; cleared here,
    /// before the new scene's first save (the world map saves as it starts). Unless that run may be the one from before a
    /// switch (<see cref="LoadBeforeSwitch"/>): then run saves stay held until the title screen is back. Null when nothing
    /// changed.
    /// </summary>
    public StaleChange? OtherSceneLoaded(string scene, double now)
    {
        if (StaleRun == null || StaleUntilTitle || OrphanRunning || Following || HoldingHostRun)
        {
            return null;
        }

        return LeftTitle($"{scene} loaded from the title screen", now);
    }

    /// <summary>True when <see cref="WatchStale"/> would look at the title screen now (so it's worth finding).</summary>
    public bool WatchesTitle(bool hostLoadQueued) =>
        StaleRun != null && !StaleUntilTitle && !OrphanRunning && !Following && !HoldingHostRun && !hostLoadQueued;

    /// <summary>
    /// While the run in memory may not be the run file's, what the title screen is doing now: once it's up, after run sync's
    /// refresh of it (and <see cref="StaleFloorSeconds"/> for anything still running from the scene before), or as soon as a
    /// run starts from it (or the game has left it), the game has the run file's run in memory again. Not while the title
    /// screen has to load first, a load of the host's run runs or waits (<paramref name="hostLoadQueued"/>), or following.
    /// Null when nothing changed.
    /// </summary>
    public StaleChange? WatchStale(TitleView title, bool hostLoadQueued, double now)
    {
        if (!WatchesTitle(hostLoadQueued))
        {
            return null;
        }

        if (!title.Loaded)
        {
            // The title screen was up since the run was marked (or it was marked there), and the game has left it.
            return LeftTitle("the game left its title screen, loading or starting the run file's run on the way", now);
        }

        if (!title.Up)
        {
            return null;
        }

        if (title.StartingRun)
        {
            return LeftTitle("a run is starting from the title screen, with the run file's run", now);
        }

        if (!RefreshWanted && now - StaleSince >= StaleFloorSeconds && title.Idle)
        {
            return Current("the title screen reloaded the run file's run");
        }

        return null;
    }

    /// <summary>A run is leaving the title screen (<paramref name="how"/>): it's the run file's, unless it may be from before a switch.</summary>
    private StaleChange LeftTitle(string how, double now)
    {
        if (LoadBeforeSwitch && RefreshWanted)
        {
            MarkStale(StartedBeforeReload, untilTitle: true, now);
            return new StaleChange(false, how, StartedBeforeReload);
        }

        return Current(how);
    }

    /// <summary>The run in memory is the run file's again (<paramref name="how"/>): run saves go through.</summary>
    private StaleChange Current(string how)
    {
        string was = StaleRun ?? string.Empty;
        StaleRun = null;
        StaleUntilTitle = false;
        LoadBeforeSwitch = false;
        return new StaleChange(true, how, was);
    }

    /// <summary>
    /// The run in memory may not be the run file's (<paramref name="why"/>): run saves are skipped until the title screen has
    /// reloaded it or a run starts from it. <paramref name="untilTitle"/>: the title screen has to load first.
    /// </summary>
    private void MarkStale(string why, bool untilTitle, double now)
    {
        StaleRun = why;
        StaleUntilTitle |= untilTitle;
        StaleSince = now;
        RefreshWanted = true; // the refresh that reloads the run file's run
    }
}

/// <summary>Where this player's own campaign run is in play, if anywhere (<see cref="RunSaveSafety.JoinRefusal"/>).</summary>
public enum OwnRunPlace
{
    /// <summary>Not in play: at the title screen, outside the campaign (an exhibition, say), or the run in memory isn't this player's own.</summary>
    None,

    /// <summary>On its world map.</summary>
    Map,

    /// <summary>In a campaign match of its own.</summary>
    Match,
}

/// <summary>What the title screen is doing, for <see cref="RunSaveSafety.WatchStale"/>.</summary>
/// <param name="Loaded">Its scene is loaded.</param>
/// <param name="Up">It's there and enabled.</param>
/// <param name="StartingRun">A run is starting from it.</param>
/// <param name="Idle">Nothing happens on it: no run starting or loading, no menu over it, not leaving.</param>
public readonly record struct TitleView(bool Loaded, bool Up, bool StartingRun, bool Idle);

/// <summary>A change to whether the run in memory is the run file's.</summary>
/// <param name="NowCurrent">True: it is now, and run saves go through. False: it may be another run, and run saves stay held until the title screen is back.</param>
/// <param name="How">What happened.</param>
/// <param name="Why">With <paramref name="NowCurrent"/>, why run saves were held before; else why they stay held.</param>
public readonly record struct StaleChange(bool NowCurrent, string How, string Why);

/// <summary>What a given-up load of the host's run finishing means (<see cref="RunSaveSafety.OrphanFinished"/>).</summary>
public enum OrphanLanded
{
    /// <summary>Nothing to do: following again, holding the host's run, or no campaign.</summary>
    Nothing,

    /// <summary>On a quiet title screen: reset the campaign state; the title reloads the run file's run.</summary>
    ResetAtTitle,

    /// <summary>Away from the title screen: run saves are held until it's back.</summary>
    HeldUntilTitle,
}
