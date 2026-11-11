using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class RunHistoryDataTests
{
    /// <summary>An entry shaped like a won three-act run (made-up values; the field set is the game's model).</summary>
    internal static RunHistoryData Sample() => new()
    {
        RunId = "9f0c2d4e-1b2a-4c3d-8e7f-0123456789ab",
        SquadId = "gauntlet",
        DifficultyLevel = 3,
        GameRule = 1,
        IsWin = true,
        IsConceded = false,
        TimestampUtc = 638_656_000_000_000_000,
        RunTime = 3456.25f,
        SquadName = "The Gauntlet",
        Team = new TeamSnapshot
        {
            Id = "team-1",
            TeamName = "Moose",
            City = "Moncton",
            Nickname = "Moose",
            LogoName = "logo_moose",
            PrimaryColor = new[] { 0.1f, 0.2f, 0.3f, 1f },
            SecondaryColor = new[] { 1f, 1f, 1f, 1f },
            Forwards = new[]
            {
                new ForwardSnapshot
                {
                    Id = "kovalski", FirstName = "Vlad", LastName = "Kovalski", HeadSkin = "h1", HelmetSkin = null, BodySkin = "b1",
                    DisplayOverall = 81, DisplaySpeed = 7, DisplayAccuracy = 8, DisplayShotPower = 9, DisplayChecking = 6,
                    AbilityId = "YoyoShot", TalentIds = new[] { "ExtraGravy", "WildShot" }, IsDeparted = false, SkaterCategory = 2,
                },
                new ForwardSnapshot { Id = "boringdude", IsDeparted = true },
            },
            Goalie = new GoalieSnapshot { Id = "goalie-1", FirstName = "Gus", LastName = "Net", TalentIds = new[] { "Fungus" } },
            RelicIds = new[] { "BeanDiet", "TribalMask" },
        },
        Scorers = new[] { new ScorerLine { SkaterId = "kovalski", DisplayName = "V. Kovalski", Goals = 14, LastGoalOrder = 3, Assists = 5, Shots = 40, Hits = 9, InjuriesReceived = 1 } },
        Matches = new[]
        {
            new MatchLine
            {
                Act = 1, TeamId = "officials", Score = "4-2", Win = true, Shootout = false, IsBoss = false, SelfScore = 4, OtherScore = 2,
                MapPointX = 2, MapPointY = 5,
                Scorers = new[] { new KeyValuePair<string, ScorerLine>("kovalski", new ScorerLine { SkaterId = "kovalski", Goals = 2 }) },
                GoalieStats = new GoalieLine { SkaterId = "goalie-1", GamesPlayed = 1, GoalsAgainst = 2, ShotsAgainst = 20, Hits = 0 },
            },
            new MatchLine { Act = 3, TeamId = "golfer", Score = "3-3 SO", Shootout = true, IsBoss = true },
        },
        Goalie = new GoalieLine { SkaterId = "goalie-1", DisplayName = "G. Net", GamesPlayed = 15, GoalsAgainst = 31, ShotsAgainst = 300, Hits = 4 },
        SticksBroken = 12,
        InjuriesInflicted = -1, // whatever the game had, sent as it was
        InjuriesReceived = 3,
        TotalCritShots = 27,
    };

    internal static byte[] Encoded(RunHistoryData entry)
    {
        var writer = new BitWriter();
        entry.Write(writer);
        return writer.ToArray();
    }

    private static bool Read(byte[] bytes, out RunHistoryData? entry)
    {
        var reader = new BitReader(bytes);
        return RunHistoryData.TryRead(ref reader, out entry);
    }

    [Fact]
    public void An_entry_round_trips_field_by_field()
    {
        RunHistoryData sample = Sample();
        byte[] bytes = Encoded(sample);
        Assert.True(Read(bytes, out RunHistoryData? read));
        Assert.Equal(bytes, Encoded(read!)); // every field is written, so equal encodings mean equal entries
        Assert.Equal("gauntlet", read!.SquadId);
        Assert.Equal(14, read.Scorers[0].Goals);
        Assert.Equal("kovalski", read.Matches[0].Scorers[0].Key);
        Assert.Null(read.Team!.Forwards[0].HelmetSkin);
        Assert.Equal(new[] { "ExtraGravy", "WildShot" }, read.Team.Forwards[0].TalentIds);
        Assert.True(read.Matches[1].IsBoss);
        Assert.Null(read.Matches[1].GoalieStats);
        Assert.Equal(-1, read.InjuriesInflicted);
        Assert.True(sample.IsWithinLimits());
    }

    [Fact]
    public void An_empty_entry_round_trips()
    {
        var empty = new RunHistoryData();
        Assert.True(Read(Encoded(empty), out RunHistoryData? read));
        Assert.Null(read!.RunId);
        Assert.Null(read.Team);
        Assert.Empty(read.Matches);
    }

    [Fact]
    public void Lists_and_text_over_the_limits_are_not_written()
    {
        Assert.Throws<ArgumentException>(() => Encoded(new RunHistoryData { SquadName = new string('x', RunHistoryData.MaxTextBytes + 1) }));
        Assert.Throws<ArgumentException>(() => Encoded(new RunHistoryData { Scorers = Enumerable.Repeat(new ScorerLine(), RunHistoryData.MaxItems + 1).ToList() }));
        Assert.False(new RunHistoryData { SquadName = new string('é', RunHistoryData.MaxTextBytes) }.IsWithinLimits()); // 2 bytes each
        Assert.False(new RunHistoryData { RunTime = float.NaN }.IsWithinLimits());
        Assert.False(Huge().IsWithinLimits()); // every list full: past the size limit
    }

    [Fact]
    public void A_truncated_entry_is_refused()
    {
        byte[] bytes = Encoded(Sample());
        for (int length = 0; length < bytes.Length - 1; length++)
        {
            Assert.False(Read(bytes[..length], out _), $"read {length} of {bytes.Length} bytes");
        }
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw()
    {
        byte[] bytes = Encoded(Sample());
        for (int i = 0; i < bytes.Length; i++)
        {
            foreach (byte flip in new byte[] { 0x01, 0x40, 0xFF })
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= flip;
                if (Read(copy, out RunHistoryData? read))
                {
                    Assert.True(read!.IsWithinLimits());
                }
            }
        }

        var random = new Random(21);
        for (int i = 0; i < 3000; i++)
        {
            byte[] junk = new byte[random.Next(0, 300)];
            random.NextBytes(junk);
            _ = Read(junk, out _);
        }
    }

    [Fact]
    public void Text_from_the_host_is_made_safe_to_show()
    {
        // The host's names reach this PC's screen and its own run history: line breaks, control characters and direction
        // overrides become '?', as in every other text a host sends; text without them is kept as it was.
        var crafted = new RunHistoryData
        {
            RunId = "run-1",
            SquadName = "Line\nbreak\u202E",
            Team = new TeamSnapshot
            {
                TeamName = "Te\u0007am",
                City = "Moncton",
                Forwards = new[] { new ForwardSnapshot { FirstName = "A\rB", LastName = "Kovalski", TalentIds = new[] { "x\u0000y" } } },
                Goalie = new GoalieSnapshot { LastName = "\u2066Net" },
                RelicIds = new[] { "Bean\u001BDiet" },
            },
            Scorers = new[] { new ScorerLine { DisplayName = "\u2028V. Kovalski" } },
            Matches = new[] { new MatchLine { Score = "4-2\n", Scorers = new[] { new KeyValuePair<string, ScorerLine>("k\u0001", new ScorerLine { DisplayName = "D\u200F" }) } } },
            Goalie = new GoalieLine { DisplayName = "G\u0085" },
        };

        Assert.True(Read(Encoded(crafted), out RunHistoryData? read));
        Assert.Equal(("run-1", "Line?break?"), (read!.RunId, read.SquadName));
        Assert.Equal(("Te?am", "Moncton"), (read.Team!.TeamName, read.Team.City));
        Assert.Equal(("A?B", "Kovalski", "x?y"), (read.Team.Forwards[0].FirstName, read.Team.Forwards[0].LastName, read.Team.Forwards[0].TalentIds[0]));
        Assert.Equal(("?Net", "Bean?Diet"), (read.Team.Goalie!.LastName, read.Team.RelicIds[0]));
        Assert.Equal(("?V. Kovalski", "4-2?", "k?", "D?", "G?"),
            (read.Scorers[0].DisplayName, read.Matches[0].Score, read.Matches[0].Scorers[0].Key, read.Matches[0].Scorers[0].Value.DisplayName, read.Goalie!.DisplayName));
        Assert.True(read.IsWithinLimits());
    }

    [Fact]
    public void Out_of_range_numbers_and_counts_are_refused()
    {
        var writer = new BitWriter();
        writer.WriteBool(false); // run id
        writer.WriteBool(false); // squad id
        writer.WriteVarInt((long)int.MaxValue + 1); // difficulty past an int
        Assert.False(Read(Pad(writer), out _));

        writer = new BitWriter();
        RunHistoryData.Text(writer, null);
        RunHistoryData.Text(writer, null);
        writer.WriteVarInt(0);
        writer.WriteVarInt(0);
        writer.WriteBool(false);
        writer.WriteBool(false);
        writer.WriteVarInt(0);
        writer.WriteSingle(0);
        RunHistoryData.Text(writer, null);
        writer.WriteBool(false); // no team
        writer.WriteVarUInt(RunHistoryData.MaxItems + 1); // too many scorers
        Assert.False(Read(Pad(writer), out _));
    }

    /// <summary>Every list as full as the limits allow, with random text: well past the size limit.</summary>
    internal static RunHistoryData Huge(int seed = 3)
    {
        var random = new Random(seed);
        string Noise() => new(Enumerable.Range(0, RunHistoryData.MaxTextBytes).Select(_ => (char)random.Next('!', '~' + 1)).ToArray());
        ScorerLine Line() => new() { SkaterId = Noise(), DisplayName = Noise(), Goals = random.Next() };
        return new RunHistoryData
        {
            RunId = Noise(),
            Matches = Enumerable.Range(0, RunHistoryData.MaxItems).Select(_ => new MatchLine
            {
                TeamId = Noise(),
                Score = Noise(),
                Scorers = Enumerable.Range(0, RunHistoryData.MaxItems).Select(_ => new KeyValuePair<string, ScorerLine>(Noise(), Line())).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Random text in lists, about <paramref name="bytes"/> encoded: within the limits but hard to compress.</summary>
    internal static RunHistoryData Noisy(int bytes, int seed)
    {
        var random = new Random(seed);
        string Noise() => new(Enumerable.Range(0, RunHistoryData.MaxTextBytes).Select(_ => (char)random.Next('!', '~' + 1)).ToArray());
        int lines = bytes / ((2 * RunHistoryData.MaxTextBytes) + 12);
        var matches = new List<MatchLine>();
        for (int left = lines; left > 0; left -= RunHistoryData.MaxItems)
        {
            matches.Add(new MatchLine
            {
                Scorers = Enumerable.Range(0, Math.Min(left, RunHistoryData.MaxItems))
                    .Select(_ => new KeyValuePair<string, ScorerLine>(Noise(), new ScorerLine { DisplayName = Noise() }))
                    .ToList(),
            });
        }

        return new RunHistoryData { Matches = matches };
    }

    private static byte[] Pad(BitWriter writer)
    {
        writer.WriteBytes(new byte[64]); // plenty of zeros after, so only the bad value can fail it
        return writer.ToArray();
    }
}
