using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Who an in-match menu's number or event is about: a side and either an index into that side's forwards (the game's
/// <c>Team.AllForwards</c>) or the side's goalie (<c>Team.goaltender</c>, <see cref="GoalieOf"/>).
/// </summary>
/// <param name="Home">The home side (else the visitors).</param>
/// <param name="Index">The forward's index in its side's list, from 0 (0 for the goalie).</param>
/// <param name="IsGoalie">The side's goalie rather than one of its forwards.</param>
public readonly record struct GameMenuRef(bool Home, int Index, bool IsGoalie = false)
{
    /// <summary>A side's goalie.</summary>
    public static GameMenuRef GoalieOf(bool home) => new(home, 0, IsGoalie: true);

    /// <summary>"h3" or "v0" for a forward, "hg" or "vg" for a goalie (how the timeline carries it).</summary>
    public override string ToString() => IsGoalie ? (Home ? "hg" : "vg") : $"{(Home ? 'h' : 'v')}{Index.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>One forward's numbers on the in-match menu: its index in its side's forwards and its counts (see <see cref="GameMenuModel.SkaterCounts"/>).</summary>
public sealed class GameMenuSkater
{
    /// <summary>A forward's numbers. Throws for a negative index or a count list of the wrong length.</summary>
    public GameMenuSkater(int index, IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (index < 0 || counts.Count != GameMenuModel.SkaterCounts)
        {
            throw new ArgumentException($"A forward has an index of 0 or more and {GameMenuModel.SkaterCounts} counts.", nameof(counts));
        }

        Index = index;
        Counts = counts.ToArray();
    }

    /// <summary>The forward's index in its side's forwards.</summary>
    public int Index { get; }

    /// <summary>Goals, assists, shots on net, passes, hits, interceptions, blocked shots (the game's <c>SkaterStats</c> the menu shows).</summary>
    public IReadOnlyList<int> Counts { get; }

    /// <summary>True when every count is 0 (such a forward isn't sent: a friend writes 0 for every forward it isn't sent).</summary>
    public bool IsZero => Counts.All(c => c == 0);
}

/// <summary>One side's numbers on the in-match menu.</summary>
public sealed class GameMenuSide
{
    /// <summary>A side. Throws for lists of the wrong length or too long.</summary>
    /// <param name="counts">The team counts (see <see cref="GameMenuModel.TeamCounts"/>).</param>
    /// <param name="timeOnAttack">Seconds on attack.</param>
    /// <param name="ids">A fingerprint of each forward's asset id (<see cref="GameMenuModel.IdPrint"/>), in the side's order: what a friend checks its own forwards against.</param>
    /// <param name="line">The forwards on the ice (indices into the side's forwards), as the menu's rows show them.</param>
    /// <param name="skaters">The forwards whose counts aren't all 0.</param>
    /// <param name="goalie">
    /// Fingerprints of the side's goalie (<see cref="GameMenuModel.GoaliePrints"/>), none when the side has no goalie (or
    /// it can't be named): what a friend checks its own goalie against.
    /// </param>
    public GameMenuSide(IReadOnlyList<int> counts, float timeOnAttack, IReadOnlyList<ushort> ids, IReadOnlyList<int> line, IReadOnlyList<GameMenuSkater> skaters,
        IReadOnlyList<ushort>? goalie = null)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(skaters);
        goalie ??= Array.Empty<ushort>();
        if (counts.Count != GameMenuModel.TeamCounts || ids.Count > GameMenuModel.MaxForwards || line.Count > GameMenuModel.MaxLine || skaters.Count > GameMenuModel.MaxForwards ||
            goalie.Count > GameMenuModel.MaxGoaliePrints)
        {
            throw new ArgumentException("A side's lists are of the wrong length.", nameof(counts));
        }

        Counts = counts.ToArray();
        TimeOnAttack = timeOnAttack;
        Ids = ids.ToArray();
        Line = line.ToArray();
        Skaters = skaters.ToArray();
        Goalie = goalie.ToArray();
    }

    /// <summary>Shots on net, passes, passes completed, hits, interceptions, blocked shots, faceoffs won (the game's <c>TeamStats</c> the menu shows).</summary>
    public IReadOnlyList<int> Counts { get; }

    /// <summary>Seconds on attack.</summary>
    public float TimeOnAttack { get; }

    /// <summary>A fingerprint of each forward's asset id, in the side's order.</summary>
    public IReadOnlyList<ushort> Ids { get; }

    /// <summary>The forwards on the ice, as indices into the side's forwards.</summary>
    public IReadOnlyList<int> Line { get; }

    /// <summary>The forwards whose counts aren't all 0.</summary>
    public IReadOnlyList<GameMenuSkater> Skaters { get; }

    /// <summary>Fingerprints of the side's goalie (none: no goalie a timeline event may name).</summary>
    public IReadOnlyList<ushort> Goalie { get; }
}

/// <summary>What happened in a timeline event.</summary>
public enum GameMenuEventKind
{
    /// <summary>A goal.</summary>
    Goal,

    /// <summary>An injury.</summary>
    Injury,
}

/// <summary>One event of the in-match menu's timeline (the game's <c>GameTimeline.GoalEventData</c> or <c>InjuryEventData</c>).</summary>
public sealed class GameMenuEvent
{
    private GameMenuEvent(GameMenuEventKind kind, int period, float time, GameMenuRef who)
    {
        Kind = kind;
        Period = period;
        Time = time;
        Who = who;
    }

    /// <summary>A goal or an injury.</summary>
    public GameMenuEventKind Kind { get; }

    /// <summary>The period it happened in.</summary>
    public int Period { get; }

    /// <summary>The game's time stamp for it, in seconds.</summary>
    public float Time { get; }

    /// <summary>The scorer, or the injured skater (a forward, or a side's goalie).</summary>
    public GameMenuRef Who { get; }

    /// <summary>Goal: the scorer's goals so far.</summary>
    public int ScorerGoals { get; private init; }

    /// <summary>Goal: the skaters who assisted (forwards, or a side's goalie), in order.</summary>
    public IReadOnlyList<GameMenuRef> Assists { get; private init; } = Array.Empty<GameMenuRef>();

    /// <summary>Goal: each assisting skater's assists so far, in the same order.</summary>
    public IReadOnlyList<int> AssistTotals { get; private init; } = Array.Empty<int>();

    /// <summary>Goal: the home side's score after it.</summary>
    public int HomeScore { get; private init; }

    /// <summary>Goal: the visitors' score after it.</summary>
    public int VisitorScore { get; private init; }

    /// <summary>Injury: the game's injury type (as a number).</summary>
    public int InjuryType { get; private init; }

    /// <summary>Injury: how long it lasts.</summary>
    public int Duration { get; private init; }

    /// <summary>A goal. Throws when the assists and their totals differ in count, or there are too many.</summary>
    public static GameMenuEvent Goal(int period, float time, GameMenuRef scorer, int scorerGoals, IReadOnlyList<GameMenuRef> assists, IReadOnlyList<int> assistTotals, int homeScore, int visitorScore)
    {
        ArgumentNullException.ThrowIfNull(assists);
        ArgumentNullException.ThrowIfNull(assistTotals);
        if (assists.Count != assistTotals.Count || assists.Count > GameMenuModel.MaxAssists)
        {
            throw new ArgumentException($"At most {GameMenuModel.MaxAssists} assists, each with its total.", nameof(assistTotals));
        }

        return new GameMenuEvent(GameMenuEventKind.Goal, period, time, scorer)
        {
            ScorerGoals = scorerGoals,
            Assists = assists.ToArray(),
            AssistTotals = assistTotals.ToArray(),
            HomeScore = homeScore,
            VisitorScore = visitorScore,
        };
    }

    /// <summary>An injury.</summary>
    public static GameMenuEvent Injury(int period, float time, GameMenuRef injured, int injuryType, int duration) =>
        new(GameMenuEventKind.Injury, period, time, injured) { InjuryType = injuryType, Duration = duration };
}

/// <summary>Which icon a row of the in-match menu's injury list shows (the game's <c>InjurySystem</c> icons).</summary>
public enum GameMenuInjuryIcon
{
    /// <summary>None (or one the host couldn't name).</summary>
    None,

    /// <summary>The injury's own icon (<c>GetInjuryIcon</c>): still injured.</summary>
    Injury,

    /// <summary>The injury's recovery icon (<c>GetRecoveryIcon</c>): healed.</summary>
    Recovery,
}

/// <summary>
/// One row of the in-match menu's injury list as the host's game drew it (the game's <c>InjuryChangeData</c>): the forward (its asset id's
/// fingerprint, <see cref="GameMenuModel.IdPrint"/>), its side, its icon (which, of which injury type, high or low definition) and its healing
/// times before and after (-1 for none before).
/// </summary>
/// <param name="Print">The forward's asset id's fingerprint.</param>
/// <param name="Home">The row's team is the home side.</param>
/// <param name="Icon">Which icon.</param>
/// <param name="Type">The icon's injury type (the game's <c>InjuryType</c> as a number; 0 with no icon).</param>
/// <param name="Hd">The icon's high-definition version.</param>
/// <param name="Previous">The healing time before (<see cref="GameMenuModel.MinHealing"/> to <see cref="GameMenuModel.MaxHealing"/>).</param>
/// <param name="New">The healing time now (the same range).</param>
public sealed record GameMenuInjuryRow(ushort Print, bool Home, GameMenuInjuryIcon Icon, int Type, bool Hd, int Previous, int New);

/// <summary>
/// The numbers of the host's in-match menu (the game's <c>GameMenu</c>: the pause menu, the end of a period, the end of a
/// match), as extra values of its <see cref="MenuData"/>: the menu's tabs and pause
/// buttons, each side's team stats, its forwards' stats by index, the forwards on the ice, the timeline's goals and
/// injuries (a forward by index, a goalie by side), and two self-checks; since format 3 the injury list's rows
/// as the host drew them (<see cref="InjuryRows"/>). A friend's game writes them into its own match objects for as long as
/// the menu draws itself, so the real menu shows the host's numbers. A friend that reads only an older format shows the
/// menu as text.
/// <para>
/// Every value is printable ASCII within <see cref="NetValue.MaxStringBytes"/>; a list too long for one value goes on in
/// numbered values ("gm.tl", "gm.tl.1", ...). The values come from the host: <see cref="TryRead"/> checks every number's
/// range and every count and returns false (with why) on anything malformed; it never throws. Whether an index names one
/// of the friend's own forwards, and whether its goalies are the host's (<see cref="SameGoalie"/>), is the game glue's
/// check.
/// </para>
/// </summary>
public sealed class GameMenuModel
{
    /// <summary>
    /// The format's version (value <see cref="VersionKey"/>): 3 may carry the injury list's rows; 2 carries
    /// goalies; version 1 named forwards only.
    /// </summary>
    public const int Version = 3;

    /// <summary>The oldest format read (1: an older host's, without goalies).</summary>
    public const int OldestVersion = 1;

    /// <summary>Value: the format's version; present only when the menu's numbers are.</summary>
    public const string VersionKey = "gm.v";

    /// <summary>Value: the tabs shown (the game's <c>GameMenu.ETab</c> flags).</summary>
    public const string TabsKey = "gm.tabs";

    /// <summary>Value: the tab open when the menu was read (one <c>ETab</c> flag, 0 for none).</summary>
    public const string TabKey = "gm.tab";

    /// <summary>Value: the pause buttons shown (the game's <c>GameMenu.EPauseMenuButton</c> flags).</summary>
    public const string ButtonsKey = "gm.buttons";

    /// <summary>Value: whether the Tycoons' sponsors show.</summary>
    public const string TycoonKey = "gm.tycoon";

    /// <summary>Value: the injury list as the host shows it, as an opaque digest (compared, never shown).</summary>
    public const string InjuriesKey = "gm.inj";

    /// <summary>Value: a check of the stats texts as the host's menu shows them (<see cref="TextCheck"/>).</summary>
    public const string CheckKey = "gm.check";

    /// <summary>Value (format 3): whether the injury list shows; present exactly when its rows are sent (<see cref="InjuryRowsKey"/>).</summary>
    public const string InjuryShownKey = "gm.inj.on";

    /// <summary>
    /// Values (format 3): the injury list's rows, ';' between rows, a list too long for one value going on in numbered values
    /// ("gm.inj.r", "gm.inj.r.1", ...): "print,side,icon,type,hd,previous,new" (print in hex, side h or v, icon i, r or -,
    /// hd 0 or 1).
    /// </summary>
    public const string InjuryRowsKey = "gm.inj.r";

    /// <summary>Most injury list rows sent (the game's list cycles through them; more are left out).</summary>
    public const int MaxInjuryRows = 16;

    /// <summary>Lowest healing time sent (-1: none before).</summary>
    public const int MinHealing = -99;

    /// <summary>Highest healing time sent.</summary>
    public const int MaxHealing = 999;

    /// <summary>Team counts per side: shots on net, passes, passes completed, hits, interceptions, blocked shots, faceoffs won.</summary>
    public const int TeamCounts = 7;

    /// <summary>Counts per forward: goals, assists, shots on net, passes, hits, interceptions, blocked shots.</summary>
    public const int SkaterCounts = 7;

    /// <summary>Most forwards on a side.</summary>
    public const int MaxForwards = 40;

    /// <summary>Most forwards on the ice on a side.</summary>
    public const int MaxLine = 8;

    /// <summary>Most timeline events.</summary>
    public const int MaxEvents = 60;

    /// <summary>Most assists on a goal.</summary>
    public const int MaxAssists = 4;

    /// <summary>Most fingerprints of a side's goalie (<see cref="GoaliePrints"/>).</summary>
    public const int MaxGoaliePrints = 2;

    /// <summary>Largest count.</summary>
    public const int MaxCount = 99_999;

    /// <summary>Largest time, in seconds.</summary>
    public const float MaxSeconds = 100_000f;

    /// <summary>Largest period.</summary>
    public const int MaxPeriod = 99;

    /// <summary>Largest injury type.</summary>
    public const int MaxInjuryType = 63;

    /// <summary>Largest injury duration.</summary>
    public const int MaxInjuryDuration = 999;

    /// <summary>Most values a list may take ("gm.tl" and up to 7 more).</summary>
    public const int MaxChunks = 8;

    /// <summary>Most values the menu's numbers take in all (the menu's data holds <see cref="MenuData.MaxValues"/>; one is its context).</summary>
    public const int MaxValues = MenuData.MaxValues - 1;

    /// <summary>All flags of the game's tabs this format knows.</summary>
    public const int TabFlags = 0xF;

    /// <summary>All flags of the game's pause buttons this format knows.</summary>
    public const int ButtonFlags = 0xFF;

    private const string TimelineKey = "gm.tl";
    private const string GoalieKey = "gm.gk";
    private const char Items = ';';
    private const char Fields = ',';
    private const char Joined = '+';

    /// <summary>The model; <paramref name="injuryRows"/> null when the injury list's rows aren't sent (format 3: they may be).</summary>
    public GameMenuModel(int tabs, int tab, int buttons, bool tycoon, GameMenuSide home, GameMenuSide visitor, IReadOnlyList<GameMenuEvent> events, string injuries, uint check,
        IReadOnlyList<GameMenuInjuryRow>? injuryRows = null, bool injuryListShown = false)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(visitor);
        ArgumentNullException.ThrowIfNull(events);
        Tabs = tabs;
        Tab = tab;
        Buttons = buttons;
        Tycoon = tycoon;
        Home = home;
        Visitor = visitor;
        Events = events.ToArray();
        Injuries = injuries ?? string.Empty;
        Check = check;
        InjuryRows = injuryRows?.ToArray();
        InjuryListShown = injuryRows != null && injuryListShown;
    }

    /// <summary>The tabs shown (<c>ETab</c> flags within <see cref="TabFlags"/>).</summary>
    public int Tabs { get; }

    /// <summary>The tab open when read (one flag, or 0).</summary>
    public int Tab { get; }

    /// <summary>The pause buttons shown (<c>EPauseMenuButton</c> flags within <see cref="ButtonFlags"/>).</summary>
    public int Buttons { get; }

    /// <summary>Whether the Tycoons' sponsors show.</summary>
    public bool Tycoon { get; }

    /// <summary>The home side's numbers.</summary>
    public GameMenuSide Home { get; }

    /// <summary>The visitors' numbers.</summary>
    public GameMenuSide Visitor { get; }

    /// <summary>The timeline's goals and injuries, in the host's order.</summary>
    public IReadOnlyList<GameMenuEvent> Events { get; }

    /// <summary>The injury list's digest ("" when not sent).</summary>
    public string Injuries { get; }

    /// <summary>The stats texts' check (<see cref="TextCheck"/>; 0 when not sent).</summary>
    public uint Check { get; }

    /// <summary>The injury list's rows as the host drew them, in its order (format 3); null when they weren't sent.</summary>
    public IReadOnlyList<GameMenuInjuryRow>? InjuryRows { get; }

    /// <summary>Whether the host's injury list shows (false when its rows weren't sent).</summary>
    public bool InjuryListShown { get; }

    /// <summary>The side <paramref name="home"/> names.</summary>
    public GameMenuSide Side(bool home) => home ? Home : Visitor;

    /// <summary>
    /// A fingerprint of a forward's id: 16 bits of its 32-bit FNV-1a hash over UTF-8 (folded). It tells whether a friend's
    /// forward at an index is the host's, not anything secret.
    /// </summary>
    public static ushort IdPrint(string? id)
    {
        uint hash = Hash32(2166136261u, id ?? string.Empty);
        return (ushort)(hash ^ (hash >> 16));
    }

    /// <summary>
    /// A goalie's fingerprints (<see cref="IdPrint"/>): of its asset id (<c>SkaterData.OriginalId</c>) and of its own id
    /// when that differs (one print when they're the same or the id is empty). Both, because a campaign run's file keeps a
    /// goalie's id but not its asset id: the run's goalie on a friend's PC has only the id the host's has, while an
    /// exhibition's or an opponent's goalie is the friend's own asset, found by the host's asset id.
    /// </summary>
    public static IReadOnlyList<ushort> GoaliePrints(string? originalId, string? id)
    {
        ushort asset = IdPrint(originalId);
        return string.IsNullOrEmpty(id) || IdPrint(id) == asset ? new[] { asset } : new[] { asset, IdPrint(id) };
    }

    /// <summary>True when a friend's goalie (<paramref name="here"/>) is the host's (<paramref name="host"/>): a fingerprint in both.</summary>
    public static bool SameGoalie(IReadOnlyList<ushort> host, IReadOnlyList<ushort> here)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        return host.Any(here.Contains);
    }

    /// <summary>
    /// A check of a menu's stats texts, in order (null counts as missing): their 32-bit FNV-1a hash over UTF-8, each text
    /// ended by a unit separator. Both games compute it over the same texts after drawing the menu; equal checks, same numbers.
    /// </summary>
    public static uint TextCheck(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        uint hash = 2166136261u;
        foreach (string? text in texts)
        {
            hash = Hash32(hash, text ?? "\u0000");
            hash = (hash ^ 0x1F) * 16777619u;
        }

        return hash;
    }

    /// <summary>
    /// Adds the model's values to <paramref name="values"/>. False (and nothing added) when a number is out of range or
    /// the values would take more than <see cref="MaxValues"/>, with why.
    /// </summary>
    public bool TryWrite(List<KeyValuePair<string, NetValue>> values, out string problem) => TryWrite(values, out problem, out _);

    /// <summary>
    /// Adds the model's values to <paramref name="values"/>, as <see cref="TryWrite(List{KeyValuePair{string, NetValue}}, out string)"/>;
    /// <paramref name="rowsLeftOut"/>: the injury list's rows didn't fit the menu's data and the rest went without them.
    /// </summary>
    public bool TryWrite(List<KeyValuePair<string, NetValue>> values, out string problem, out bool rowsLeftOut)
    {
        ArgumentNullException.ThrowIfNull(values);
        rowsLeftOut = false;
        problem = Problem();
        if (problem.Length > 0)
        {
            return false;
        }

        var added = new List<KeyValuePair<string, NetValue>>
        {
            new(VersionKey, NetValue.FromInt(Version)),
            new(TabsKey, NetValue.FromInt(Tabs)),
            new(TabKey, NetValue.FromInt(Tab)),
            new(ButtonsKey, NetValue.FromInt(Buttons)),
            new(TycoonKey, NetValue.FromBool(Tycoon)),
        };
        foreach ((string side, GameMenuSide numbers) in new[] { ("h", Home), ("v", Visitor) })
        {
            added.Add(new($"gm.team.{side}", NetValue.FromString(string.Join(Fields, numbers.Counts.Select(Whole).Append(Seconds(numbers.TimeOnAttack))))));
            added.Add(new($"gm.ids.{side}", NetValue.FromString(string.Join(Fields, numbers.Ids.Select(Hex)))));
            if (numbers.Goalie.Count > 0)
            {
                added.Add(new($"{GoalieKey}.{side}", NetValue.FromString(string.Join(Fields, numbers.Goalie.Select(Hex)))));
            }

            added.Add(new($"gm.line.{side}", NetValue.FromString(string.Join(Fields, numbers.Line.Select(Whole)))));
            IEnumerable<string> skaters = numbers.Skaters.Where(s => !s.IsZero).Select(s => $"{Whole(s.Index)}:{string.Join(Fields, s.Counts.Select(Whole))}");
            if (!TryChunk($"gm.sk.{side}", skaters, added))
            {
                problem = "a side's forwards don't fit the menu's data";
                return false;
            }
        }

        if (!TryChunk(TimelineKey, Events.Select(Format), added))
        {
            problem = $"the timeline's {Events.Count} events don't fit the menu's data";
            return false;
        }

        if (Injuries.Length > 0)
        {
            added.Add(new(InjuriesKey, NetValue.FromString(Injuries)));
        }

        added.Add(new(CheckKey, NetValue.FromInt(Check)));
        if (InjuryRows != null)
        {
            // The rows only when they fit: the rest of the menu goes without them otherwise (a friend then compares digests).
            var rows = new List<KeyValuePair<string, NetValue>> { new(InjuryShownKey, NetValue.FromBool(InjuryListShown)) };
            if (TryChunk(InjuryRowsKey, InjuryRows.Select(Format), rows) && added.Count + rows.Count <= MaxValues)
            {
                added.AddRange(rows);
            }
            else
            {
                rowsLeftOut = true;
            }
        }

        if (added.Count > MaxValues)
        {
            problem = $"the menu's numbers take {added.Count} values, over {MaxValues}";
            return false;
        }

        values.AddRange(added);
        return true;
    }

    /// <summary>
    /// The model in <paramref name="data"/>, or false with why: no model (an older host, or one that couldn't send it), a
    /// version this build doesn't read, or anything malformed or out of range. Never throws.
    /// </summary>
    public static bool TryRead(MenuData data, out GameMenuModel? model, out string problem)
    {
        model = null;
        if (data == null || !data.TryGet(VersionKey, out NetValue version) || version.Kind != NetValueKind.Whole)
        {
            problem = "the host's menu came without its numbers";
            return false;
        }

        if (version.WholeValue is < OldestVersion or > Version)
        {
            problem = $"the host's menu numbers are in format {version.WholeValue}, not {OldestVersion} to {Version}";
            return false;
        }

        try
        {
            return TryParse(data, (int)version.WholeValue, out model, out problem);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Every parse below is a Try form; this is the backstop the rule asks for (decoders never throw).
            problem = "the host's menu numbers are malformed";
            return false;
        }
    }

    private static bool TryParse(MenuData data, int version, out GameMenuModel? model, out string problem)
    {
        model = null;
        long tabs = data.Whole(TabsKey, -1);
        long tab = data.Whole(TabKey, -1);
        long buttons = data.Whole(ButtonsKey, -1);
        if (tabs is < 0 or > TabFlags || tab is not (0 or 1 or 2 or 4 or 8) || buttons is < 0 or > ButtonFlags || !data.TryGet(TycoonKey, out NetValue tycoon) || tycoon.Kind != NetValueKind.Flag)
        {
            problem = "the host's menu tabs or buttons are malformed";
            return false;
        }

        if (!TryReadSide(data, "h", out GameMenuSide? home, out problem) || !TryReadSide(data, "v", out GameMenuSide? visitor, out problem))
        {
            return false;
        }

        var events = new List<GameMenuEvent>();
        if (!TryJoin(data, TimelineKey, out string timeline))
        {
            problem = "the host's timeline is malformed";
            return false;
        }

        foreach (string item in Split(timeline, Items))
        {
            if (events.Count >= MaxEvents || !TryParseEvent(item, out GameMenuEvent? parsed))
            {
                problem = events.Count >= MaxEvents ? $"the host's timeline has over {MaxEvents} events" : "the host's timeline is malformed";
                return false;
            }

            events.Add(parsed!);
        }

        string injuries = data.Text(InjuriesKey);
        long check = data.Whole(CheckKey, -1);
        if (check is < 0 or > uint.MaxValue)
        {
            problem = "the host's menu check is malformed";
            return false;
        }

        if (!TryReadInjuryRows(data, version, out List<GameMenuInjuryRow>? rows, out bool shown, out problem))
        {
            return false;
        }

        var read = new GameMenuModel((int)tabs, (int)tab, (int)buttons, tycoon.WholeValue != 0, home!, visitor!, events, injuries, (uint)check, rows, shown);
        problem = read.Problem();
        if (problem.Length > 0)
        {
            // Every range was checked above; what's left is cross-checks (an event naming a forward past its side's list).
            problem = $"the host's menu numbers don't add up ({problem})";
            return false;
        }

        model = read;
        return true;
    }

    private static bool TryReadSide(MenuData data, string side, out GameMenuSide? numbers, out string problem)
    {
        numbers = null;
        string name = side == "h" ? "home" : "visitors'";
        string[] team = data.Text($"gm.team.{side}").Split(Fields);
        if (team.Length != TeamCounts + 1)
        {
            problem = $"the host's {name} team numbers are malformed";
            return false;
        }

        var counts = new int[TeamCounts];
        for (int i = 0; i < TeamCounts; i++)
        {
            if (!TryCount(team[i], MaxCount, out counts[i]))
            {
                problem = $"the host's {name} team numbers are malformed";
                return false;
            }
        }

        if (!TrySeconds(team[TeamCounts], out float attack))
        {
            problem = $"the host's {name} time on attack is malformed";
            return false;
        }

        if (!TryPrints(data.Text($"gm.ids.{side}"), MaxForwards, out List<ushort> ids))
        {
            problem = $"the host's {name} forwards are malformed";
            return false;
        }

        if (!TryPrints(data.Text($"{GoalieKey}.{side}"), MaxGoaliePrints, out List<ushort> goalie))
        {
            problem = $"the host's {name} goalie is malformed";
            return false;
        }

        var line = new List<int>();
        foreach (string entry in Split(data.Text($"gm.line.{side}"), Fields))
        {
            if (line.Count >= MaxLine || !TryCount(entry, ids.Count - 1, out int index) || line.Contains(index))
            {
                problem = $"the host's {name} line is malformed";
                return false;
            }

            line.Add(index);
        }

        var skaters = new List<GameMenuSkater>();
        if (!TryJoin(data, $"gm.sk.{side}", out string all))
        {
            problem = $"the host's {name} forwards' numbers are malformed";
            return false;
        }

        foreach (string entry in Split(all, Items))
        {
            if (skaters.Count >= MaxForwards || !TryParseSkater(entry, ids.Count, out GameMenuSkater? skater) || skaters.Any(s => s.Index == skater!.Index))
            {
                problem = $"the host's {name} forwards' numbers are malformed";
                return false;
            }

            skaters.Add(skater!);
        }

        numbers = new GameMenuSide(counts, attack, ids, line, skaters, goalie);
        problem = string.Empty;
        return true;
    }

    /// <summary>Fingerprints as the host wrote them (<see cref="Hex"/>, joined by commas; "" for none), at most <paramref name="max"/>.</summary>
    private static bool TryPrints(string text, int max, out List<ushort> prints)
    {
        prints = new List<ushort>();
        foreach (string print in Split(text, Fields))
        {
            if (prints.Count >= max || print.Length is 0 or > 4 || !print.All(Uri.IsHexDigit) ||
                !ushort.TryParse(print, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
            {
                return false;
            }

            prints.Add(value);
        }

        return true;
    }

    private static bool TryParseSkater(string entry, int forwards, out GameMenuSkater? skater)
    {
        skater = null;
        int colon = entry.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !TryCount(entry[..colon], forwards - 1, out int index))
        {
            return false;
        }

        string[] parts = entry[(colon + 1)..].Split(Fields);
        if (parts.Length != SkaterCounts)
        {
            return false;
        }

        var counts = new int[SkaterCounts];
        for (int i = 0; i < SkaterCounts; i++)
        {
            if (!TryCount(parts[i], MaxCount, out counts[i]))
            {
                return false;
            }
        }

        skater = new GameMenuSkater(index, counts);
        return true;
    }

    private static bool TryParseEvent(string item, out GameMenuEvent? parsed)
    {
        parsed = null;
        string[] f = item.Split(Fields);
        if (f.Length < 4 || !TryCount(f[1], MaxPeriod, out int period) || !TrySeconds(f[2], out float time) || !TryRef(f[3], out GameMenuRef who))
        {
            return false;
        }

        switch (f[0])
        {
            case "G" when f.Length == 9:
                var assists = new List<GameMenuRef>();
                var totals = new List<int>();
                foreach (string assist in Split(f[5], Joined))
                {
                    if (assists.Count >= MaxAssists || !TryRef(assist, out GameMenuRef helper))
                    {
                        return false;
                    }

                    assists.Add(helper);
                }

                foreach (string total in Split(f[6], Joined))
                {
                    if (totals.Count >= MaxAssists || !TryCount(total, MaxCount, out int value))
                    {
                        return false;
                    }

                    totals.Add(value);
                }

                if (assists.Count != totals.Count || !TryCount(f[4], MaxCount, out int goals) || !TryCount(f[7], MaxCount, out int homeScore) || !TryCount(f[8], MaxCount, out int visitorScore))
                {
                    return false;
                }

                parsed = GameMenuEvent.Goal(period, time, who, goals, assists, totals, homeScore, visitorScore);
                return true;
            case "I" when f.Length == 6:
                if (!TryCount(f[4], MaxInjuryType, out int type) || !TryCount(f[5], MaxInjuryDuration, out int duration))
                {
                    return false;
                }

                parsed = GameMenuEvent.Injury(period, time, who, type, duration);
                return true;
            default:
                return false;
        }
    }

    private static string Format(GameMenuInjuryRow r) => string.Join(Fields, Hex(r.Print), r.Home ? "h" : "v",
        r.Icon switch { GameMenuInjuryIcon.Injury => "i", GameMenuInjuryIcon.Recovery => "r", _ => "-" }, Whole(r.Type), r.Hd ? "1" : "0", Whole(r.Previous), Whole(r.New));

    private static string Format(GameMenuEvent e) => e.Kind == GameMenuEventKind.Goal
        ? string.Join(Fields, "G", Whole(e.Period), Seconds(e.Time), e.Who.ToString(), Whole(e.ScorerGoals), string.Join(Joined, e.Assists.Select(a => a.ToString())),
            string.Join(Joined, e.AssistTotals.Select(Whole)), Whole(e.HomeScore), Whole(e.VisitorScore))
        : string.Join(Fields, "I", Whole(e.Period), Seconds(e.Time), e.Who.ToString(), Whole(e.InjuryType), Whole(e.Duration));

    /// <summary>
    /// Why this model can't be written ("" when it can): a number out of range, an index past its side's forwards, a
    /// goalie named on a side that has none.
    /// </summary>
    private string Problem()
    {
        if (Tabs is < 0 or > TabFlags || Tab is not (0 or 1 or 2 or 4 or 8) || Buttons is < 0 or > ButtonFlags)
        {
            return "the menu's tabs or buttons are out of range";
        }

        foreach (GameMenuSide side in new[] { Home, Visitor })
        {
            if (side.Counts.Any(c => c is < 0 or > MaxCount) || !InSeconds(side.TimeOnAttack) || side.Line.Any(i => i < 0 || i >= side.Ids.Count) ||
                side.Line.Distinct().Count() != side.Line.Count ||
                side.Skaters.Any(s => s.Index >= side.Ids.Count || s.Counts.Any(c => c is < 0 or > MaxCount)) ||
                side.Skaters.Select(s => s.Index).Distinct().Count() != side.Skaters.Count)
            {
                return "a side's numbers are out of range";
            }
        }

        if (Events.Count > MaxEvents)
        {
            return $"the timeline has over {MaxEvents} events";
        }

        foreach (GameMenuEvent e in Events)
        {
            bool known = e.Period is >= 0 and <= MaxPeriod && InSeconds(e.Time) && Names(e.Who) && e.ScorerGoals is >= 0 and <= MaxCount &&
                e.Assists.All(Names) && e.AssistTotals.All(t => t is >= 0 and <= MaxCount) && e.HomeScore is >= 0 and <= MaxCount &&
                e.VisitorScore is >= 0 and <= MaxCount && e.InjuryType is >= 0 and <= MaxInjuryType && e.Duration is >= 0 and <= MaxInjuryDuration;
            if (!known)
            {
                return "a timeline event is out of range";
            }
        }

        if (Injuries.Length > NetValue.MaxStringBytes || Injuries.Any(c => c is < ' ' or > '~'))
        {
            return "the injury list's digest isn't short printable text";
        }

        if (InjuryRows != null && (InjuryRows.Count > MaxInjuryRows || InjuryRows.Any(r => !InRange(r))))
        {
            return InjuryRows.Count > MaxInjuryRows ? $"the injury list has over {MaxInjuryRows} rows" : "an injury list row is out of range";
        }

        return string.Empty;
    }

    /// <summary>True for a row within the format's ranges (no icon: type 0, not high definition).</summary>
    private static bool InRange(GameMenuInjuryRow row) =>
        row != null && row.Icon is GameMenuInjuryIcon.None or GameMenuInjuryIcon.Injury or GameMenuInjuryIcon.Recovery && row.Type is >= 0 and <= MaxInjuryType &&
        (row.Icon != GameMenuInjuryIcon.None || (row.Type == 0 && !row.Hd)) && row.Previous is >= MinHealing and <= MaxHealing && row.New is >= MinHealing and <= MaxHealing;

    /// <summary>
    /// The injury list's rows of a format 3 model (null, and nothing shown, when none were sent or the format is older); false,
    /// with why, for anything malformed.
    /// </summary>
    private static bool TryReadInjuryRows(MenuData data, int version, out List<GameMenuInjuryRow>? rows, out bool shown, out string problem)
    {
        rows = null;
        shown = false;
        problem = string.Empty;
        if (version < 3 || !data.TryGet(InjuryShownKey, out NetValue on))
        {
            return true;
        }

        if (on.Kind != NetValueKind.Flag || !TryJoin(data, InjuryRowsKey, out string joined))
        {
            problem = "the host's injury list is malformed";
            return false;
        }

        rows = new List<GameMenuInjuryRow>();
        foreach (string item in Split(joined, Items))
        {
            if (rows.Count >= MaxInjuryRows || !TryParseInjuryRow(item, out GameMenuInjuryRow? row))
            {
                problem = rows.Count >= MaxInjuryRows ? $"the host's injury list has over {MaxInjuryRows} rows" : "the host's injury list is malformed";
                rows = null;
                return false;
            }

            rows.Add(row!);
        }

        shown = on.WholeValue != 0;
        return true;
    }

    /// <summary>A row as <see cref="Format(GameMenuInjuryRow)"/> writes it: "print,side,icon,type,hd,previous,new".</summary>
    private static bool TryParseInjuryRow(string item, out GameMenuInjuryRow? row)
    {
        row = null;
        string[] f = item.Split(Fields);
        if (f.Length != 7 || !TryPrints(f[0], 1, out List<ushort> print) || print.Count != 1 || f[1] is not ("h" or "v") || f[2] is not ("i" or "r" or "-") ||
            !TryCount(f[3], MaxInjuryType, out int type) || f[4] is not ("0" or "1") || !TrySigned(f[5], MinHealing, MaxHealing, out int previous) ||
            !TrySigned(f[6], MinHealing, MaxHealing, out int now))
        {
            return false;
        }

        var icon = f[2] switch
        {
            "i" => GameMenuInjuryIcon.Injury,
            "r" => GameMenuInjuryIcon.Recovery,
            _ => GameMenuInjuryIcon.None,
        };
        var read = new GameMenuInjuryRow(print[0], f[1] == "h", icon, type, f[4] == "1", previous, now);
        if (!InRange(read))
        {
            return false;
        }

        row = read;
        return true;
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>: an optional '-', then digits (no leading zero), at most 9 of them.</summary>
    private static bool TrySigned(string text, int min, int max, out int value)
    {
        value = 0;
        bool negative = text.StartsWith('-');
        string digits = negative ? text[1..] : text;
        if (!TryCount(digits, int.MaxValue, out int magnitude) || (negative && magnitude == 0))
        {
            return false;
        }

        value = negative ? -magnitude : magnitude;
        return value >= min && value <= max;
    }

    private bool Names(GameMenuRef who) => who.IsGoalie
        ? who.Index == 0 && Side(who.Home).Goalie.Count > 0
        : who.Index >= 0 && who.Index < Side(who.Home).Ids.Count;

    /// <summary>Packs <paramref name="items"/> into values of at most <see cref="NetValue.MaxStringBytes"/> ("key", "key.1", ...). None for no items.</summary>
    private static bool TryChunk(string key, IEnumerable<string> items, List<KeyValuePair<string, NetValue>> values)
    {
        var chunk = new StringBuilder();
        var chunks = new List<KeyValuePair<string, NetValue>>();
        bool Flush()
        {
            if (chunk.Length == 0)
            {
                return true;
            }

            if (chunks.Count >= MaxChunks)
            {
                return false;
            }

            chunks.Add(new(ChunkKey(key, chunks.Count), NetValue.FromString(chunk.ToString())));
            chunk.Clear();
            return true;
        }

        foreach (string item in items)
        {
            if (item.Length is 0 or > NetValue.MaxStringBytes || item.Contains(Items, StringComparison.Ordinal))
            {
                return false;
            }

            if (chunk.Length > 0 && chunk.Length + 1 + item.Length > NetValue.MaxStringBytes && !Flush())
            {
                return false;
            }

            if (chunk.Length > 0)
            {
                chunk.Append(Items);
            }

            chunk.Append(item);
        }

        if (!Flush())
        {
            return false;
        }

        values.AddRange(chunks);
        return true;
    }

    /// <summary>The values of a list joined again ("" for none). False for a gap before a later value, or one that isn't text.</summary>
    private static bool TryJoin(MenuData data, string key, out string joined)
    {
        var all = new StringBuilder();
        bool ended = false;
        for (int i = 0; i < MaxChunks; i++)
        {
            if (!data.TryGet(ChunkKey(key, i), out NetValue value))
            {
                ended = true;
                continue;
            }

            if (ended || value.Kind != NetValueKind.Text || string.IsNullOrEmpty(value.Text))
            {
                joined = string.Empty;
                return false;
            }

            if (all.Length > 0)
            {
                all.Append(Items);
            }

            all.Append(value.Text);
        }

        joined = all.ToString();
        return true;
    }

    private static string ChunkKey(string key, int index) => index == 0 ? key : $"{key}.{index.ToString(CultureInfo.InvariantCulture)}";

    private static string[] Split(string text, char separator) => text.Length == 0 ? Array.Empty<string>() : text.Split(separator);

    /// <summary>A forward ("h3", "v0") or a side's goalie ("hg", "vg"), as <see cref="GameMenuRef.ToString"/> writes them.</summary>
    private static bool TryRef(string text, out GameMenuRef who)
    {
        who = default;
        if (text is "hg" or "vg")
        {
            who = GameMenuRef.GoalieOf(text[0] == 'h');
            return true;
        }

        if (text.Length < 2 || text[0] is not ('h' or 'v') || !TryCount(text[1..], MaxForwards - 1, out int index))
        {
            return false;
        }

        who = new GameMenuRef(text[0] == 'h', index);
        return true;
    }

    /// <summary>A whole number of 0 to <paramref name="max"/>: digits only (no sign, space or leading zero), at most 9 of them.</summary>
    private static bool TryCount(string text, int max, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 9 || (text.Length > 1 && text[0] == '0') || !text.All(c => c is >= '0' and <= '9'))
        {
            return false;
        }

        value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value <= max;
    }

    /// <summary>Seconds as the host wrote them (<see cref="Seconds"/>): a finite number from 0 to <see cref="MaxSeconds"/>.</summary>
    private static bool TrySeconds(string text, out float seconds)
    {
        seconds = 0;
        return text.Length is > 0 and <= 16 && text.All(c => c is (>= '0' and <= '9') or '.' or 'E' or '-' or '+') &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && InSeconds(seconds);
    }

    private static bool InSeconds(float seconds) => float.IsFinite(seconds) && seconds >= 0 && seconds <= MaxSeconds;

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(ushort print) => print.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Seconds in the shortest text that reads back as the same float (so a friend's menu rounds them as the host's does).</summary>
    private static string Seconds(float seconds) => seconds.ToString("R", CultureInfo.InvariantCulture);

    private static uint Hash32(uint hash, string text)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}
