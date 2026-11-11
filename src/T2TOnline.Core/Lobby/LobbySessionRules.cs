using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>What the host's title is doing, read from the game: plain values, so what they mean can be tested without it.</summary>
public readonly record struct LobbyTitleView
{
    /// <summary>The title's scene is loaded.</summary>
    public bool SceneLoaded { get; init; }

    /// <summary>The title's script is up and has started (not just its scene).</summary>
    public bool Up { get; init; }

    /// <summary><c>m_IsExiting</c>: the title is leaving for a game. Only ever set.</summary>
    public bool Leaving { get; init; }

    /// <summary><c>m_IsStartingRun</c>: the game's New Run flow runs (its prompt, the squad screen).</summary>
    public bool StartingRun { get; init; }

    /// <summary>One of the game's start menus is on <c>m_MenusOverSelf</c> (<see cref="LobbySignals.IsStartMenu"/>).</summary>
    public bool StartMenuOver { get; init; }

    /// <summary>Another menu than the lobby's own shell is on <c>m_MenusOverSelf</c> (Settings, Glossary, a run history).</summary>
    public bool OtherMenuOver { get; init; }

    /// <summary><c>m_IsClickingButton</c>: a button's click is running.</summary>
    public bool Clicking { get; init; }

    /// <summary><c>m_IsRefreshingCampaignState</c>: the title loads the run for its campaign buttons.</summary>
    public bool Refreshing { get; init; }

    /// <summary><c>IsPreparingTitleMenus</c>: the title's warm-ups run (they open and close menus over it).</summary>
    public bool Preparing { get; init; }

    /// <summary>A prompt or menu the title's list doesn't name is on top of everything, the shell aside (only read during a start: it costs more).</summary>
    public bool MenuOnTop { get; init; }
}

/// <summary>What a request from Steam to join a lobby does now (<see cref="LobbySignals.JoinVerdict"/>).</summary>
public enum SteamJoinVerdict
{
    /// <summary>Join now.</summary>
    JoinNow,

    /// <summary>Keep it in the inbox: a run, an exhibition or a menu of this player's own, or a game under way in a session.</summary>
    Hold,

    /// <summary>Ask first (the game's yes/no, Stay first): this PC is in a lobby, or hosts one with nobody in it.</summary>
    AskToSwitch,

    /// <summary>Refuse with words: leaving would drop the friends this PC hosts.</summary>
    RefuseHosting,

    /// <summary>It's the lobby this PC is in, or on its way into.</summary>
    AlreadyThere,
}

/// <summary>What decides a request from Steam to join (<see cref="LobbySignals.JoinVerdict"/>).</summary>
public readonly record struct SteamJoinFacts
{
    /// <summary>The request names the lobby this PC is in or joining.</summary>
    public bool SameLobby { get; init; }

    /// <summary>This PC hosts.</summary>
    public bool Hosting { get; init; }

    /// <summary>Hosting: friends seated or with a kept seat.</summary>
    public int FriendsSeated { get; init; }

    /// <summary>This PC is in a friend's lobby, or joining one.</summary>
    public bool InFriendsLobby { get; init; }

    /// <summary>A game is under way in this PC's session (a start, a match, a run): the session can't be left for a prompt.</summary>
    public bool GameUnderWay { get; init; }

    /// <summary>Out of a session: the title is idle, with nothing of this player's own in play or open over it.</summary>
    public bool AtIdleTitle { get; init; }
}

/// <summary>
/// The facts <see cref="LobbySignals.OwnTitleBack"/> reads: a friend's game is home when it's on its own title with the follower arrived, and no host's match or load left here.
/// A run of the host's still held then isn't asked about: the follower puts one away on its way to the title, so what's left is a run-start preview from a menu the host backed out
/// of (its title view changed nothing, so nothing moved here), and the play period's end puts that away.
/// </summary>
public readonly record struct OwnTitleFacts
{
    /// <summary>The campaign's following runs.</summary>
    public bool Following { get; init; }

    /// <summary>The host's newest view is the title.</summary>
    public bool TargetIsTitle { get; init; }

    /// <summary>The follower arrived on the title (its move has no answer pending).</summary>
    public bool ShowingTitle { get; init; }

    /// <summary>The active scene is the title's, and its script is up.</summary>
    public bool OnTitleScene { get; init; }

    /// <summary>A scene load is under way.</summary>
    public bool SceneLoading { get; init; }

    /// <summary>A host's match is still loading or playing here.</summary>
    public bool MatchBusy { get; init; }
}

/// <summary>
/// The caller's rules of <see cref="LobbyFlow"/> that can be tested without the game: which of the level-triggered title events is true now, when a friend's game is home,
/// what a request from Steam does, and the ribbon's numbers.
/// </summary>
public static class LobbySignals
{
    private const string ControllerScreen = "ControllerAssignment";

    /// <summary>True for a menu of the game's start flows on the title's list: its controller screen, which Play Now and the title's other match rows open. The lobby's prompts, Settings and Glossary aren't.</summary>
    public static bool IsStartMenu(string? id) => id != null && id.StartsWith(ControllerScreen, StringComparison.Ordinal);

    /// <summary>
    /// The title event that's true now, or null when none is (the scene is in, its script isn't up yet; or the title is busy with something that isn't a game's start, as
    /// Settings over it is). Outside a Start <see cref="LobbyEvent.TitleBusy"/> is an implicit Start, so it's raised only for what starts a game; during a start any menu
    /// over the title holds off the back-out, and so does the game's own start still waiting to be called (<paramref name="gameStartDue"/>): it can't have been backed out of yet.
    /// </summary>
    public static LobbyEvent? TitleEvent(in LobbyTitleView view, LobbyHostState state, bool gameStartDue = false)
    {
        if (!view.SceneLoaded)
        {
            return LobbyEvent.SceneLeftTitle;
        }

        if (!view.Up)
        {
            return null;
        }

        if (view.Leaving || view.StartingRun || view.StartMenuOver)
        {
            return LobbyEvent.TitleBusy;
        }

        bool idle = !view.OtherMenuOver && !view.Clicking && !view.Refreshing && !view.Preparing && !view.MenuOnTop;
        if (idle)
        {
            return gameStartDue ? LobbyEvent.TitleBusy : LobbyEvent.TitleIdle;
        }

        return state == LobbyHostState.GameFlow ? LobbyEvent.TitleBusy : null;
    }

    /// <summary>True while a friend's game is back at its own title: <see cref="LobbyEvent.OwnTitleBack"/> is level-triggered, so it's asked each tick.</summary>
    public static bool OwnTitleBack(in OwnTitleFacts facts) =>
        facts.Following && facts.TargetIsTitle && facts.ShowingTitle && facts.OnTitleScene && !facts.SceneLoading && !facts.MatchBusy;

    /// <summary>
    /// Host: true when a friend saying <paramref name="stage"/> needs the campaign link though no ack gives it: they dropped in a game and came back after this host's game went home,
    /// so they still follow it (their game goes home only on the title view, <see cref="OwnTitleBack"/>), and what it credited while they were away comes only with the link.
    /// </summary>
    public static bool FollowsAfterGame(LobbyMemberStage stage, LobbyHostState host, bool following) =>
        !following && stage is LobbyMemberStage.Playing or LobbyMemberStage.Returning && host is LobbyHostState.Open or LobbyHostState.Counting or LobbyHostState.Returning;

    /// <summary>
    /// Host: true while a friend's virtual controller stays neutral whatever their stream says: the lobby is up and no game is starting. An honest friend sends nothing before
    /// <c>BeginPlay</c>, which needs the board's Start (D91), so this only ever holds a friend on their way home, or a modified client that would press the game's press-to-join
    /// from the lobby. Not the one-PC test's friend: its pad goes through this PC's own session in the lobby too.
    /// </summary>
    public static bool HoldsFriendInput(LobbyHostState host, bool onePcFriend) => !onePcFriend && host is LobbyHostState.Open or LobbyHostState.Counting;

    /// <summary>
    /// True when the game's own start can be called, and Start can be offered: the title's script is up, its warm-ups are over (<c>HostMenus.Showing</c> takes a controller screen
    /// opened during them for a warm-up, so friends would see nothing until the match), and <paramref name="runFileReady"/> (<c>RunSync.ReadyToStart</c>: idle, on the chosen slot).
    /// </summary>
    public static bool StartCanRun(in LobbyTitleView view, bool runFileReady) => view.Up && !view.Preparing && runFileReady;

    /// <summary>What a request from Steam to join a lobby does now. One inbox for everything: nothing of this player's is ever destroyed by a press in Steam.</summary>
    public static SteamJoinVerdict JoinVerdict(in SteamJoinFacts facts)
    {
        if (facts.SameLobby)
        {
            return SteamJoinVerdict.AlreadyThere;
        }

        if (facts.Hosting && facts.FriendsSeated > 0)
        {
            return SteamJoinVerdict.RefuseHosting;
        }

        if (facts.Hosting || facts.InFriendsLobby)
        {
            return facts.GameUnderWay ? SteamJoinVerdict.Hold : SteamJoinVerdict.AskToSwitch;
        }

        return facts.AtIdleTitle ? SteamJoinVerdict.JoinNow : SteamJoinVerdict.Hold;
    }

    /// <summary>A byte for the board's numbers: a measurement that doesn't fit is the most a byte says, never another number.</summary>
    public static byte Clamp(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);
}

/// <summary>What the game reported of the last game, as the session gathers it (<see cref="LobbyResults"/>). Nothing here is guessed: what wasn't reported stays absent.</summary>
public readonly record struct LobbyGameFacts
{
    /// <summary>The game was started in this mode.</summary>
    public LobbyMode Mode { get; init; }

    /// <summary>The match ended by itself (<c>GameManager.isGameEnded</c>), not by a quit.</summary>
    public bool MatchEnded { get; init; }

    /// <summary>Our score as last read, or -1 when never read.</summary>
    public int OurScore { get; init; }

    /// <summary>Their score as last read, or -1 when never read.</summary>
    public int TheirScore { get; init; }

    /// <summary>Players on both sides of a match: nobody's "we", so won or lost means nothing and <see cref="OurScore"/> is simply the home side's.</summary>
    public bool Versus { get; init; }

    /// <summary>The game's <c>EndOfGameContext.IsRunWin</c> was seen.</summary>
    public bool RunWon { get; init; }

    /// <summary>The game's <c>IsRunFailed</c> or <c>IsRunConceded</c> was seen, or its run was marked failed.</summary>
    public bool RunFailed { get; init; }

    /// <summary>A run was in play during this game.</summary>
    public bool RunSeen { get; init; }

    /// <summary>The run's act as last read, or 0.</summary>
    public int Act { get; init; }

    /// <summary>The run's floor as last read, or 0.</summary>
    public int Floor { get; init; }

    /// <summary>The chosen slot holds a run the game can go on with, back at the title.</summary>
    public bool RunStillInSlot { get; init; }
}

/// <summary>The ribbon on the lobby after a game: only what the game really reported, and none at all when it reported nothing.</summary>
public static class LobbyResults
{
    /// <summary>The ribbon for <paramref name="facts"/>.</summary>
    public static LobbyResult Of(in LobbyGameFacts facts)
    {
        if (facts.Mode == LobbyMode.QuickGame)
        {
            if (!facts.MatchEnded || facts.OurScore < 0 || facts.TheirScore < 0)
            {
                return LobbyResult.None; // quit mid-match: no result to report
            }

            LobbyResultKind kind = facts.Versus ? LobbyResultKind.MatchScore
                : facts.OurScore > facts.TheirScore ? LobbyResultKind.MatchWon
                : facts.OurScore < facts.TheirScore ? LobbyResultKind.MatchLost
                : LobbyResultKind.MatchTied;
            return new LobbyResult(kind, LobbySignals.Clamp(facts.OurScore), LobbySignals.Clamp(facts.TheirScore));
        }

        if (!facts.RunSeen)
        {
            return LobbyResult.None;
        }

        byte act = LobbySignals.Clamp(facts.Act);
        byte floor = LobbySignals.Clamp(facts.Floor);
        if (facts.RunWon)
        {
            return new LobbyResult(LobbyResultKind.RunWon, act, floor);
        }

        if (facts.RunFailed || !facts.RunStillInSlot)
        {
            return new LobbyResult(LobbyResultKind.RunOver, act, floor);
        }

        return new LobbyResult(LobbyResultKind.RunSaved, act, floor);
    }

    /// <summary>
    /// <paramref name="facts"/> with the game's run as read (its act, floor and failed mark), when it is a run on the map: act 1 or later.
    /// A run's end puts a blank run in its place before the title loads (<c>RunEndHelper.EndRun</c> calls <c>CampaignState.ResetAllData</c>:
    /// a new id, act 0, floor 0), and a run not started yet is act 0 too; a read of either keeps what the game last reported.
    /// </summary>
    public static LobbyGameFacts WithRun(in LobbyGameFacts facts, int act, int floor, bool failed)
    {
        if (act < 1)
        {
            return facts;
        }

        return facts with { RunSeen = true, Act = act, Floor = floor, RunFailed = facts.RunFailed || failed };
    }
}

/// <summary>One friend the host's session knows, for <see cref="LobbyPeople.Of"/>: plain values from its session and pads.</summary>
public readonly record struct LobbySeated
{
    /// <summary>Their Steam ID.</summary>
    public ulong SteamId { get; init; }

    /// <summary>Their name.</summary>
    public string Name { get; init; }

    /// <summary>Their member message, when <see cref="HasMember"/>.</summary>
    public LobbyMemberMessage Member { get; init; }

    /// <summary>A member message has come since they last said hello.</summary>
    public bool HasMember { get; init; }

    /// <summary>What they play with.</summary>
    public InputKind DeviceKind { get; init; }

    /// <summary>Their pad's family.</summary>
    public byte DeviceFamily { get; init; }

    /// <summary>Their round trip in ms, or negative when unknown.</summary>
    public int PingMs { get; init; }

    /// <summary>Their stream's loss in percent.</summary>
    public double LossPercent { get; init; }
}

/// <summary>Who shows on the host's board, from what the session knows: the seated, the kept, those arriving, those invited. Each once, in the most present stage they're in.</summary>
public static class LobbyPeople
{
    /// <summary>The people for a board.</summary>
    /// <param name="seated">Friends in the session.</param>
    /// <param name="kept">Friends who dropped, with their seat kept: their ID, name and seconds left.</param>
    /// <param name="members">Who's in the Steam lobby; those not seated are arriving.</param>
    /// <param name="invited">Who an invite went to in the last 2 minutes, with their names.</param>
    /// <param name="self">This PC's Steam ID: never a friend.</param>
    /// <param name="phase">The board's phase.</param>
    /// <param name="nameOf">Names someone only the lobby knows.</param>
    /// <param name="refused">
    /// Who the host won't count as arriving: removed or refused, or just said goodbye while Steam's list still names them
    /// (<see cref="LeavingMembers"/>). In the Steam lobby they aren't arriving, and hold no seat or Start. Null: nobody.
    /// </param>
    public static IReadOnlyList<LobbyPerson> Of(
        IReadOnlyList<LobbySeated> seated,
        IReadOnlyList<(ulong SteamId, string Name, int SecondsLeft)> kept,
        IReadOnlyList<ulong> members,
        IReadOnlyList<(ulong SteamId, string Name)> invited,
        ulong self,
        LobbyPhase phase,
        Func<ulong, string> nameOf,
        Func<ulong, bool>? refused = null)
    {
        ArgumentNullException.ThrowIfNull(seated);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(invited);
        ArgumentNullException.ThrowIfNull(nameOf);

        var people = new List<LobbyPerson>(seated.Count + kept.Count);
        var known = new HashSet<ulong> { self };
        foreach (LobbySeated friend in seated)
        {
            if (!known.Add(friend.SteamId))
            {
                continue;
            }

            LobbyMemberMessage member = friend.Member;
            people.Add(new LobbyPerson
            {
                SteamId = friend.SteamId,
                Name = friend.Name,
                Stage = friend.HasMember ? LobbyPerson.StageOf(member, phase) : SeatStage.Here,
                Ready = friend.HasMember && member.Ready,
                DeviceKind = friend.DeviceKind,
                DeviceFamily = friend.DeviceFamily,
                PingMs = friend.PingMs,
                LossPercent = friend.LossPercent,
            });
        }

        foreach ((ulong id, string name, int seconds) in kept)
        {
            if (known.Add(id))
            {
                people.Add(new LobbyPerson { SteamId = id, Name = name, Stage = SeatStage.Reconnecting, ReconnectSeconds = seconds, PingMs = -1 });
            }
        }

        foreach (ulong id in members)
        {
            if (id != 0 && refused?.Invoke(id) != true && known.Add(id))
            {
                people.Add(new LobbyPerson { SteamId = id, Name = nameOf(id), Stage = SeatStage.Joining, PingMs = -1 });
            }
        }

        foreach ((ulong id, string name) in invited)
        {
            if (id != 0 && known.Add(id))
            {
                people.Add(new LobbyPerson { SteamId = id, Name = name, Stage = SeatStage.Invited, PingMs = -1 });
            }
        }

        return people;
    }

    /// <summary>
    /// The first of <paramref name="state"/>'s people who aren't here, whom Start waits for; null when everyone is. Start's log
    /// lines name them by their seat's Steam ID as a tag, never by the seat's name: that is the name as the lobby draws it, with
    /// any character its font lacks taken out, a form the log's backstop doesn't know.
    /// </summary>
    public static LobbySeat? FirstNotHere(LobbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.NotHere.Count > 0 ? state.NotHere[0] : null;
    }
}
