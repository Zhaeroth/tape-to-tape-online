using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A run history entry as plain data: the fields of the game's <c>State.RunHistory.RunHistoryEntryModel</c> and the
/// models it holds (team snapshot, skaters, goalie, scorers, match scores), so the shared run's entry can go into every
/// player's own run history (D42). Sent as these fields and nothing else: a friend's game builds the
/// game's own model from them, so a host can't make it create anything but a run history entry.
/// </summary>
public sealed class RunHistoryData
{
    /// <summary>Longest text field, in UTF-8 bytes.</summary>
    public const int MaxTextBytes = 128;

    /// <summary>Most items in one list (skaters, talents, relics, scorers, matches).</summary>
    public const int MaxItems = 64;

    /// <summary>The run's id.</summary>
    public string? RunId { get; init; }

    /// <summary>The squad's id.</summary>
    public string? SquadId { get; init; }

    /// <summary>The run's difficulty.</summary>
    public int DifficultyLevel { get; init; }

    /// <summary>The run's game rule (the game's <c>GameRule</c> number).</summary>
    public int GameRule { get; init; }

    /// <summary>The run was won.</summary>
    public bool IsWin { get; init; }

    /// <summary>The run was conceded.</summary>
    public bool IsConceded { get; init; }

    /// <summary>When the run ended (the game's UTC timestamp).</summary>
    public long TimestampUtc { get; init; }

    /// <summary>The run's time, in seconds.</summary>
    public float RunTime { get; init; }

    /// <summary>The squad's name as shown.</summary>
    public string? SquadName { get; init; }

    /// <summary>The team at the run's end.</summary>
    public TeamSnapshot? Team { get; init; }

    /// <summary>The run's scorers.</summary>
    public IReadOnlyList<ScorerLine> Scorers { get; init; } = Array.Empty<ScorerLine>();

    /// <summary>The run's matches.</summary>
    public IReadOnlyList<MatchLine> Matches { get; init; } = Array.Empty<MatchLine>();

    /// <summary>The goalie's run statistics.</summary>
    public GoalieLine? Goalie { get; init; }

    /// <summary>Sticks broken in the run.</summary>
    public int SticksBroken { get; init; }

    /// <summary>Injuries inflicted in the run.</summary>
    public int InjuriesInflicted { get; init; }

    /// <summary>Injuries received in the run.</summary>
    public int InjuriesReceived { get; init; }

    /// <summary>Critical shots in the run.</summary>
    public int TotalCritShots { get; init; }

    /// <summary>How the run ended, for logs: "won", "conceded" or "lost".</summary>
    public string Outcome => IsWin ? "won" : IsConceded ? "conceded" : "lost";

    /// <summary>What the run was, for logs: "run 9f0c...: won, squad gauntlet difficulty 2, 6 skater(s), 9 match(es), run time 31:04".</summary>
    public string Words()
    {
        double seconds = float.IsFinite(RunTime) && RunTime > 0 ? RunTime : 0;
        return $"run {Diagnostics.HostText.KeyForLog(RunId)}: {Outcome}, squad {Diagnostics.HostText.KeyForLog(SquadId)} difficulty {DifficultyLevel}, " +
            $"{Team?.Forwards.Count ?? 0} skater(s), {Matches.Count} match(es), run time {(int)(seconds / 60)}:{(int)(seconds % 60):00}";
    }

    /// <summary>
    /// True when every list and text is within the limits, the numbers are finite and the whole comes to at most
    /// <see cref="ProgressLimits.MaxRunHistoryBytes"/> (so it can be sent).
    /// </summary>
    public bool IsWithinLimits()
    {
        var writer = new BitWriter(1024);
        try
        {
            Write(writer);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (writer.ByteLength > ProgressLimits.MaxRunHistoryBytes)
        {
            return false;
        }

        var reader = new BitReader(writer.AsSpan());
        return TryRead(ref reader, out _);
    }

    /// <summary>Writes the entry. Throws <see cref="ArgumentException"/> for a list or text over the limits.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Text(writer, RunId);
        Text(writer, SquadId);
        writer.WriteVarInt(DifficultyLevel);
        writer.WriteVarInt(GameRule);
        writer.WriteBool(IsWin);
        writer.WriteBool(IsConceded);
        writer.WriteVarInt(TimestampUtc);
        writer.WriteSingle(RunTime);
        Text(writer, SquadName);
        writer.WriteBool(Team != null);
        Team?.Write(writer);
        Items(writer, Scorers, (w, s) => s.Write(w));
        Items(writer, Matches, (w, m) => m.Write(w));
        writer.WriteBool(Goalie != null);
        Goalie?.Write(writer);
        writer.WriteVarInt(SticksBroken);
        writer.WriteVarInt(InjuriesInflicted);
        writer.WriteVarInt(InjuriesReceived);
        writer.WriteVarInt(TotalCritShots);
    }

    /// <summary>Reads an entry. False for anything malformed or over the limits. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out RunHistoryData? entry)
    {
        entry = null;
        bool ok = true;
        string? runId = Text(ref reader), squadId = Text(ref reader);
        int difficulty = Whole(ref reader, ref ok), rule = Whole(ref reader, ref ok);
        bool win = reader.ReadBool(), conceded = reader.ReadBool();
        long timestamp = reader.ReadVarInt();
        float runTime = reader.ReadSingle();
        string? squadName = Text(ref reader);
        TeamSnapshot? team = null;
        if (reader.ReadBool() && !TeamSnapshot.TryRead(ref reader, out team))
        {
            return false;
        }

        if (!TryItems(ref reader, ScorerLine.TryRead, out List<ScorerLine>? scorers) ||
            !TryItems(ref reader, MatchLine.TryRead, out List<MatchLine>? matches))
        {
            return false;
        }

        GoalieLine? goalie = null;
        if (reader.ReadBool() && !GoalieLine.TryRead(ref reader, out goalie))
        {
            return false;
        }

        int sticks = Whole(ref reader, ref ok), inflicted = Whole(ref reader, ref ok), received = Whole(ref reader, ref ok), crits = Whole(ref reader, ref ok);
        if (!ok || reader.IsOverrun || !float.IsFinite(runTime))
        {
            return false;
        }

        entry = new RunHistoryData
        {
            RunId = runId,
            SquadId = squadId,
            DifficultyLevel = difficulty,
            GameRule = rule,
            IsWin = win,
            IsConceded = conceded,
            TimestampUtc = timestamp,
            RunTime = runTime,
            SquadName = squadName,
            Team = team,
            Scorers = scorers!,
            Matches = matches!,
            Goalie = goalie,
            SticksBroken = sticks,
            InjuriesInflicted = inflicted,
            InjuriesReceived = received,
            TotalCritShots = crits,
        };
        return true;
    }

    /// <summary>Writes optional text (a flag, then the text): over <see cref="MaxTextBytes"/> throws.</summary>
    internal static void Text(BitWriter writer, string? text)
    {
        writer.WriteBool(text != null);
        if (text == null)
        {
            return;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
        {
            throw new ArgumentException($"Text over {MaxTextBytes} bytes.", nameof(text));
        }

        writer.WriteShortString(text, MaxTextBytes);
    }

    /// <summary>
    /// Reads optional text; an overlong length sets the reader overrun. The text is a host's and reaches this PC's screen and
    /// its own run history, so it's made safe to show as every other text a host sends is (<see cref="Diagnostics.SafeText"/>:
    /// control characters, line breaks and direction overrides become '?'; nothing is cut, the length already being capped).
    /// </summary>
    internal static string? Text(ref BitReader reader) => reader.ReadBool() ? Diagnostics.SafeText.Clean(reader.ReadShortString(MaxTextBytes), MaxTextBytes) : null;

    /// <summary>Reads a whole number that must fit an <see cref="int"/>; one that doesn't clears <paramref name="ok"/>.</summary>
    internal static int Whole(ref BitReader reader, ref bool ok)
    {
        long value = reader.ReadVarInt();
        if (value is < int.MinValue or > int.MaxValue)
        {
            ok = false;
            return 0;
        }

        return (int)value;
    }

    /// <summary>Writes a list: its count, then each item. Over <see cref="MaxItems"/> throws.</summary>
    internal static void Items<T>(BitWriter writer, IReadOnlyList<T>? items, Action<BitWriter, T> write)
    {
        items ??= Array.Empty<T>();
        if (items.Count > MaxItems)
        {
            throw new ArgumentException($"More than {MaxItems} items.", nameof(items));
        }

        writer.WriteVarUInt((ulong)items.Count);
        foreach (T item in items)
        {
            write(writer, item);
        }
    }

    /// <summary>Reads a list written by <see cref="Items{T}(BitWriter, IReadOnlyList{T}, Action{BitWriter, T})"/>.</summary>
    internal static bool TryItems<T>(ref BitReader reader, ItemReader<T> read, out List<T>? items)
    {
        items = null;
        ulong count = reader.ReadVarUInt();
        if (reader.IsOverrun || count > MaxItems)
        {
            return false;
        }

        var list = new List<T>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            if (!read(ref reader, out T? item) || reader.IsOverrun)
            {
                return false;
            }

            list.Add(item!);
        }

        items = list;
        return true;
    }

    /// <summary>Text lists (talents, relics).</summary>
    internal static void Texts(BitWriter writer, IReadOnlyList<string>? texts) => Items(writer, texts, (w, t) => Text(w, t ?? string.Empty));

    /// <summary>Reads a text list.</summary>
    internal static bool TryTexts(ref BitReader reader, out List<string>? texts) => TryItems(ref reader, ReadText, out texts);

    private static bool ReadText(ref BitReader reader, out string? text)
    {
        text = Text(ref reader) ?? string.Empty;
        return !reader.IsOverrun;
    }

}

/// <summary>Reads one list item. False when it's malformed.</summary>
/// <typeparam name="T">The item type.</typeparam>
public delegate bool ItemReader<T>(ref BitReader reader, out T? item);

/// <summary>The game's <c>RunHistoryTeamModel</c>: the team at the run's end.</summary>
public sealed class TeamSnapshot
{
    /// <summary>The team's id.</summary>
    public string? Id { get; init; }

    /// <summary>The team's name.</summary>
    public string? TeamName { get; init; }

    /// <summary>The team's city.</summary>
    public string? City { get; init; }

    /// <summary>The team's nickname.</summary>
    public string? Nickname { get; init; }

    /// <summary>The team's logo (asset name).</summary>
    public string? LogoName { get; init; }

    /// <summary>The players' primary colour, RGBA.</summary>
    public IReadOnlyList<float> PrimaryColor { get; init; } = new float[4];

    /// <summary>The players' secondary colour, RGBA.</summary>
    public IReadOnlyList<float> SecondaryColor { get; init; } = new float[4];

    /// <summary>The skaters.</summary>
    public IReadOnlyList<ForwardSnapshot> Forwards { get; init; } = Array.Empty<ForwardSnapshot>();

    /// <summary>The goalie.</summary>
    public GoalieSnapshot? Goalie { get; init; }

    /// <summary>The team's relics, by id.</summary>
    public IReadOnlyList<string> RelicIds { get; init; } = Array.Empty<string>();

    /// <summary>Writes the snapshot (see <see cref="RunHistoryData.Write"/>).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RunHistoryData.Text(writer, Id);
        RunHistoryData.Text(writer, TeamName);
        RunHistoryData.Text(writer, City);
        RunHistoryData.Text(writer, Nickname);
        RunHistoryData.Text(writer, LogoName);
        WriteColor(writer, PrimaryColor);
        WriteColor(writer, SecondaryColor);
        RunHistoryData.Items(writer, Forwards, (w, f) => f.Write(w));
        writer.WriteBool(Goalie != null);
        Goalie?.Write(writer);
        RunHistoryData.Texts(writer, RelicIds);
    }

    /// <summary>Reads a snapshot. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out TeamSnapshot? team)
    {
        team = null;
        string? id = RunHistoryData.Text(ref reader), name = RunHistoryData.Text(ref reader), city = RunHistoryData.Text(ref reader);
        string? nickname = RunHistoryData.Text(ref reader), logo = RunHistoryData.Text(ref reader);
        float[] primary = ReadColor(ref reader), secondary = ReadColor(ref reader);
        if (!RunHistoryData.TryItems(ref reader, ForwardSnapshot.TryRead, out List<ForwardSnapshot>? forwards))
        {
            return false;
        }

        GoalieSnapshot? goalie = null;
        if (reader.ReadBool() && !GoalieSnapshot.TryRead(ref reader, out goalie))
        {
            return false;
        }

        if (!RunHistoryData.TryTexts(ref reader, out List<string>? relics) || reader.IsOverrun ||
            !Array.TrueForAll(primary, float.IsFinite) || !Array.TrueForAll(secondary, float.IsFinite))
        {
            return false;
        }

        team = new TeamSnapshot
        {
            Id = id,
            TeamName = name,
            City = city,
            Nickname = nickname,
            LogoName = logo,
            PrimaryColor = primary,
            SecondaryColor = secondary,
            Forwards = forwards!,
            Goalie = goalie,
            RelicIds = relics!,
        };
        return true;
    }

    private static void WriteColor(BitWriter writer, IReadOnlyList<float> color)
    {
        for (int i = 0; i < 4; i++)
        {
            writer.WriteSingle(color != null && i < color.Count ? color[i] : 0f);
        }
    }

    private static float[] ReadColor(ref BitReader reader) => new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
}

/// <summary>The game's <c>RunHistoryForwardModel</c>: one skater.</summary>
public sealed class ForwardSnapshot
{
    /// <summary>The skater's id.</summary>
    public string? Id { get; init; }

    /// <summary>First name.</summary>
    public string? FirstName { get; init; }

    /// <summary>Last name.</summary>
    public string? LastName { get; init; }

    /// <summary>Head skin.</summary>
    public string? HeadSkin { get; init; }

    /// <summary>Helmet skin.</summary>
    public string? HelmetSkin { get; init; }

    /// <summary>Body skin.</summary>
    public string? BodySkin { get; init; }

    /// <summary>Overall rating shown.</summary>
    public int DisplayOverall { get; init; }

    /// <summary>Speed shown.</summary>
    public int DisplaySpeed { get; init; }

    /// <summary>Accuracy shown.</summary>
    public int DisplayAccuracy { get; init; }

    /// <summary>Shot power shown.</summary>
    public int DisplayShotPower { get; init; }

    /// <summary>Checking shown.</summary>
    public int DisplayChecking { get; init; }

    /// <summary>The skater's ability, by id.</summary>
    public string? AbilityId { get; init; }

    /// <summary>The skater's talents, by id.</summary>
    public IReadOnlyList<string> TalentIds { get; init; } = Array.Empty<string>();

    /// <summary>The skater left the team during the run.</summary>
    public bool IsDeparted { get; init; }

    /// <summary>The game's <c>SkaterCategory</c> number.</summary>
    public int SkaterCategory { get; init; }

    /// <summary>Writes the skater.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RunHistoryData.Text(writer, Id);
        RunHistoryData.Text(writer, FirstName);
        RunHistoryData.Text(writer, LastName);
        RunHistoryData.Text(writer, HeadSkin);
        RunHistoryData.Text(writer, HelmetSkin);
        RunHistoryData.Text(writer, BodySkin);
        writer.WriteVarInt(DisplayOverall);
        writer.WriteVarInt(DisplaySpeed);
        writer.WriteVarInt(DisplayAccuracy);
        writer.WriteVarInt(DisplayShotPower);
        writer.WriteVarInt(DisplayChecking);
        RunHistoryData.Text(writer, AbilityId);
        RunHistoryData.Texts(writer, TalentIds);
        writer.WriteBool(IsDeparted);
        writer.WriteVarInt(SkaterCategory);
    }

    /// <summary>Reads a skater. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ForwardSnapshot? forward)
    {
        forward = null;
        bool ok = true;
        string? id = RunHistoryData.Text(ref reader), first = RunHistoryData.Text(ref reader), last = RunHistoryData.Text(ref reader);
        string? head = RunHistoryData.Text(ref reader), helmet = RunHistoryData.Text(ref reader), body = RunHistoryData.Text(ref reader);
        int overall = RunHistoryData.Whole(ref reader, ref ok), speed = RunHistoryData.Whole(ref reader, ref ok), accuracy = RunHistoryData.Whole(ref reader, ref ok);
        int power = RunHistoryData.Whole(ref reader, ref ok), checking = RunHistoryData.Whole(ref reader, ref ok);
        string? ability = RunHistoryData.Text(ref reader);
        if (!RunHistoryData.TryTexts(ref reader, out List<string>? talents))
        {
            return false;
        }

        bool departed = reader.ReadBool();
        int category = RunHistoryData.Whole(ref reader, ref ok);
        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        forward = new ForwardSnapshot
        {
            Id = id,
            FirstName = first,
            LastName = last,
            HeadSkin = head,
            HelmetSkin = helmet,
            BodySkin = body,
            DisplayOverall = overall,
            DisplaySpeed = speed,
            DisplayAccuracy = accuracy,
            DisplayShotPower = power,
            DisplayChecking = checking,
            AbilityId = ability,
            TalentIds = talents!,
            IsDeparted = departed,
            SkaterCategory = category,
        };
        return true;
    }
}

/// <summary>The game's <c>RunHistoryGoalieModel</c>: the goalie.</summary>
public sealed class GoalieSnapshot
{
    /// <summary>The goalie's id.</summary>
    public string? Id { get; init; }

    /// <summary>First name.</summary>
    public string? FirstName { get; init; }

    /// <summary>Last name.</summary>
    public string? LastName { get; init; }

    /// <summary>Head skin.</summary>
    public string? HeadSkin { get; init; }

    /// <summary>The goalie's ability, by id.</summary>
    public string? AbilityId { get; init; }

    /// <summary>The goalie's talents, by id.</summary>
    public IReadOnlyList<string> TalentIds { get; init; } = Array.Empty<string>();

    /// <summary>Writes the goalie.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RunHistoryData.Text(writer, Id);
        RunHistoryData.Text(writer, FirstName);
        RunHistoryData.Text(writer, LastName);
        RunHistoryData.Text(writer, HeadSkin);
        RunHistoryData.Text(writer, AbilityId);
        RunHistoryData.Texts(writer, TalentIds);
    }

    /// <summary>Reads a goalie. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out GoalieSnapshot? goalie)
    {
        goalie = null;
        string? id = RunHistoryData.Text(ref reader), first = RunHistoryData.Text(ref reader), last = RunHistoryData.Text(ref reader);
        string? head = RunHistoryData.Text(ref reader), ability = RunHistoryData.Text(ref reader);
        if (!RunHistoryData.TryTexts(ref reader, out List<string>? talents) || reader.IsOverrun)
        {
            return false;
        }

        goalie = new GoalieSnapshot { Id = id, FirstName = first, LastName = last, HeadSkin = head, AbilityId = ability, TalentIds = talents! };
        return true;
    }
}

/// <summary>The game's <c>ScorersStats</c>: one skater's line in the run or a match.</summary>
public sealed class ScorerLine
{
    /// <summary>The skater's id.</summary>
    public string? SkaterId { get; init; }

    /// <summary>The name shown.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Goals.</summary>
    public int Goals { get; init; }

    /// <summary>The order of the skater's last goal.</summary>
    public int LastGoalOrder { get; init; }

    /// <summary>Assists.</summary>
    public int Assists { get; init; }

    /// <summary>Shots.</summary>
    public int Shots { get; init; }

    /// <summary>Hits.</summary>
    public int Hits { get; init; }

    /// <summary>Injuries received.</summary>
    public int InjuriesReceived { get; init; }

    /// <summary>Writes the line.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RunHistoryData.Text(writer, SkaterId);
        RunHistoryData.Text(writer, DisplayName);
        writer.WriteVarInt(Goals);
        writer.WriteVarInt(LastGoalOrder);
        writer.WriteVarInt(Assists);
        writer.WriteVarInt(Shots);
        writer.WriteVarInt(Hits);
        writer.WriteVarInt(InjuriesReceived);
    }

    /// <summary>Reads a line. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ScorerLine? line)
    {
        bool ok = true;
        string? id = RunHistoryData.Text(ref reader), name = RunHistoryData.Text(ref reader);
        line = new ScorerLine
        {
            SkaterId = id,
            DisplayName = name,
            Goals = RunHistoryData.Whole(ref reader, ref ok),
            LastGoalOrder = RunHistoryData.Whole(ref reader, ref ok),
            Assists = RunHistoryData.Whole(ref reader, ref ok),
            Shots = RunHistoryData.Whole(ref reader, ref ok),
            Hits = RunHistoryData.Whole(ref reader, ref ok),
            InjuriesReceived = RunHistoryData.Whole(ref reader, ref ok),
        };
        return ok && !reader.IsOverrun;
    }
}

/// <summary>The game's <c>MatchScore</c>: one match of the run.</summary>
public sealed class MatchLine
{
    /// <summary>The act.</summary>
    public int Act { get; init; }

    /// <summary>The opponent's id.</summary>
    public string? TeamId { get; init; }

    /// <summary>The score as shown.</summary>
    public string? Score { get; init; }

    /// <summary>Won.</summary>
    public bool Win { get; init; }

    /// <summary>Decided by a shootout.</summary>
    public bool Shootout { get; init; }

    /// <summary>A boss match.</summary>
    public bool IsBoss { get; init; }

    /// <summary>The team's goals.</summary>
    public int SelfScore { get; init; }

    /// <summary>The opponent's goals.</summary>
    public int OtherScore { get; init; }

    /// <summary>The node on the map, x.</summary>
    public float MapPointX { get; init; }

    /// <summary>The node on the map, y.</summary>
    public float MapPointY { get; init; }

    /// <summary>The match's scorers, by the game's key (a skater id).</summary>
    public IReadOnlyList<KeyValuePair<string, ScorerLine>> Scorers { get; init; } = Array.Empty<KeyValuePair<string, ScorerLine>>();

    /// <summary>The goalie's match statistics.</summary>
    public GoalieLine? GoalieStats { get; init; }

    /// <summary>Writes the match.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarInt(Act);
        RunHistoryData.Text(writer, TeamId);
        RunHistoryData.Text(writer, Score);
        writer.WriteBool(Win);
        writer.WriteBool(Shootout);
        writer.WriteBool(IsBoss);
        writer.WriteVarInt(SelfScore);
        writer.WriteVarInt(OtherScore);
        writer.WriteSingle(MapPointX);
        writer.WriteSingle(MapPointY);
        RunHistoryData.Items(writer, Scorers, (w, s) =>
        {
            RunHistoryData.Text(w, s.Key ?? string.Empty);
            s.Value.Write(w);
        });
        writer.WriteBool(GoalieStats != null);
        GoalieStats?.Write(writer);
    }

    /// <summary>Reads a match. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out MatchLine? match)
    {
        match = null;
        bool ok = true;
        int act = RunHistoryData.Whole(ref reader, ref ok);
        string? team = RunHistoryData.Text(ref reader), score = RunHistoryData.Text(ref reader);
        bool win = reader.ReadBool(), shootout = reader.ReadBool(), boss = reader.ReadBool();
        int self = RunHistoryData.Whole(ref reader, ref ok), other = RunHistoryData.Whole(ref reader, ref ok);
        float x = reader.ReadSingle(), y = reader.ReadSingle();
        if (!RunHistoryData.TryItems(ref reader, ReadScorer, out List<KeyValuePair<string, ScorerLine>>? scorers))
        {
            return false;
        }

        GoalieLine? goalie = null;
        if (reader.ReadBool() && !GoalieLine.TryRead(ref reader, out goalie))
        {
            return false;
        }

        if (!ok || reader.IsOverrun || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return false;
        }

        match = new MatchLine
        {
            Act = act,
            TeamId = team,
            Score = score,
            Win = win,
            Shootout = shootout,
            IsBoss = boss,
            SelfScore = self,
            OtherScore = other,
            MapPointX = x,
            MapPointY = y,
            Scorers = scorers!,
            GoalieStats = goalie,
        };
        return true;
    }

    private static bool ReadScorer(ref BitReader reader, out KeyValuePair<string, ScorerLine> item)
    {
        string key = RunHistoryData.Text(ref reader) ?? string.Empty;
        bool ok = ScorerLine.TryRead(ref reader, out ScorerLine? line);
        item = new KeyValuePair<string, ScorerLine>(key, line ?? new ScorerLine());
        return ok && !reader.IsOverrun;
    }
}

/// <summary>The game's <c>GoalieStats</c>: the goalie's line in the run or a match.</summary>
public sealed class GoalieLine
{
    /// <summary>The goalie's id.</summary>
    public string? SkaterId { get; init; }

    /// <summary>The name shown.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Games played.</summary>
    public int GamesPlayed { get; init; }

    /// <summary>Goals against.</summary>
    public int GoalsAgainst { get; init; }

    /// <summary>Shots against.</summary>
    public int ShotsAgainst { get; init; }

    /// <summary>Hits.</summary>
    public int Hits { get; init; }

    /// <summary>Writes the line.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RunHistoryData.Text(writer, SkaterId);
        RunHistoryData.Text(writer, DisplayName);
        writer.WriteVarInt(GamesPlayed);
        writer.WriteVarInt(GoalsAgainst);
        writer.WriteVarInt(ShotsAgainst);
        writer.WriteVarInt(Hits);
    }

    /// <summary>Reads a line. False for anything malformed. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out GoalieLine? line)
    {
        bool ok = true;
        string? id = RunHistoryData.Text(ref reader), name = RunHistoryData.Text(ref reader);
        line = new GoalieLine
        {
            SkaterId = id,
            DisplayName = name,
            GamesPlayed = RunHistoryData.Whole(ref reader, ref ok),
            GoalsAgainst = RunHistoryData.Whole(ref reader, ref ok),
            ShotsAgainst = RunHistoryData.Whole(ref reader, ref ok),
            Hits = RunHistoryData.Whole(ref reader, ref ok),
        };
        return ok && !reader.IsOverrun;
    }
}
