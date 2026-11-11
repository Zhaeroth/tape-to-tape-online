using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NurseStepsTests
{
    /// <summary>A sacrifice's takeover, five steps in: every kind of step that has an argument or an action.</summary>
    public static NurseStepsData Sample() => new()
    {
        Procedure = 4,
        StartStage = 1,
        StartFocus = 0,
        Steps = new[]
        {
            new NurseStep { Number = 3, Kind = NurseStepKind.Direction, Arg = 2, Stage = 1, Focus = 1 },
            new NurseStep { Number = 4, Kind = NurseStepKind.Focus, Arg = 2, Stage = 1, Focus = 2 },
            new NurseStep { Number = 5, Kind = NurseStepKind.Press, Action = "UIDetails", Stage = 1, Focus = 2 },
            new NurseStep { Number = 6, Kind = NurseStepKind.Release, Action = "UIDetails", Stage = 1, Focus = 2 },
            new NurseStep { Number = 7, Kind = NurseStepKind.Confirm, Reveal = true, Stage = 12, Focus = -1 },
        },
    };

    public static byte[] Encoded(NurseStepsData steps)
    {
        var writer = new BitWriter();
        steps.Write(writer);
        return writer.ToArray();
    }

    private static NurseStepsData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = NurseStepsData.TryRead(ref reader, out NurseStepsData? steps);
        Assert.Equal(ok, steps != null);
        return ok && reader.RemainingBits < 8 ? steps : null;
    }

    [Fact]
    public void Steps_round_trip_field_by_field()
    {
        NurseStepsData read = Read(Encoded(Sample()))!;
        Assert.Equal((4, 1, 0, 5, 7), (read.Procedure, read.StartStage, read.StartFocus, read.Steps.Count, read.Newest));
        for (int i = 0; i < 5; i++)
        {
            NurseStep want = Sample().Steps[i];
            NurseStep got = read.Steps[i];
            Assert.Equal((want.Number, want.Kind, want.Arg, want.Action, want.Reveal, want.Stage, want.Focus), (got.Number, got.Kind, got.Arg, got.Action, got.Reveal, got.Stage, got.Focus));
        }

        Assert.Equal(NurseStepKind.Focus, read.Find(4)!.Kind);
        Assert.Null(read.Find(2));
        Assert.Null(read.Find(8));
        Assert.Equal("procedure 4 (stage 1, option 0 at the start), steps 3 to 7", read.ToString());
        Assert.Equal("step 7: Confirm (during the reveal) (stage 12, option -1)", read.Steps[4].ToString());
        Assert.Equal("step 5: Press UIDetails (stage 1, option 2)", read.Steps[2].ToString());

        // The takeover's start, with no steps yet.
        var start = new NurseStepsData { Procedure = 0, StartStage = -1, StartFocus = -1 };
        NurseStepsData first = Read(Encoded(start))!;
        Assert.Equal((0, 0, -1, -1, "procedure 0 (stage -1, option -1 at the start), no steps yet"), (first.Steps.Count, first.Newest, first.StartStage, first.StartFocus, first.ToString()));
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        NurseStepsData With(params NurseStep[] steps) => new() { Procedure = 1, Steps = steps };

        yield return new object[] { "a procedure past the cap", new NurseStepsData { Procedure = 64 } };
        yield return new object[] { "a first stage past the cap", new NurseStepsData { StartStage = 64 } };
        yield return new object[] { "a first focus below none", new NurseStepsData { StartFocus = -2 } };
        yield return new object[] { "too many steps", With(Enumerable.Range(1, NurseStepsData.MaxSteps + 1).Select(n => new NurseStep { Number = n, Kind = NurseStepKind.Back }).ToArray()) };
        yield return new object[] { "a missing step", With(new NurseStep[] { null! }) };
        yield return new object[] { "steps out of order", With(new NurseStep { Number = 2, Kind = NurseStepKind.Back }, new NurseStep { Number = 4, Kind = NurseStepKind.Back }) };
        yield return new object[] { "step number 0", With(new NurseStep { Number = 0, Kind = NurseStepKind.Back }) };
        yield return new object[] { "a step number past 65535", With(new NurseStep { Number = 65536, Kind = NurseStepKind.Back }) };
        yield return new object[] { "no kind", With(new NurseStep { Kind = NurseStepKind.None }) };
        yield return new object[] { "an unknown kind", With(new NurseStep { Kind = (NurseStepKind)10 }) };
        yield return new object[] { "an argument on a confirm", With(new NurseStep { Kind = NurseStepKind.Confirm, Arg = 1 }) };
        yield return new object[] { "an argument past the cap", With(new NurseStep { Kind = NurseStepKind.Direction, Arg = 256 }) };
        yield return new object[] { "a press with no action", With(new NurseStep { Kind = NurseStepKind.Press }) };
        yield return new object[] { "a confirm with an action", With(new NurseStep { Kind = NurseStepKind.Confirm, Action = "x" }) };
        yield return new object[] { "a long action", With(new NurseStep { Kind = NurseStepKind.Release, Action = new string('a', NurseStep.MaxActionBytes + 1) }) };
        yield return new object[] { "an action that isn't printable", With(new NurseStep { Kind = NurseStepKind.Press, Action = "é" }) };
        yield return new object[] { "a stage past the cap", With(new NurseStep { Kind = NurseStepKind.Back, Stage = 64 }) };
        yield return new object[] { "a focus past the cap", With(new NurseStep { Kind = NurseStepKind.Back, Focus = 256 }) };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Steps_out_of_their_limits_are_refused_when_written_and_never_sent(string what, NurseStepsData steps)
    {
        Assert.NotNull(steps.Check());
        Assert.ThrowsAny<ArgumentException>(() => steps.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.NurseSteps(3, steps));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Crafted_steps_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(Sample());
        Assert.NotNull(Read(good));
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        // More steps than the window; a first stage byte past the cap; a step whose kind is unknown.
        Assert.Null(Read(Crafted(4, 0, 0, NurseStepsData.MaxSteps + 1)));
        Assert.Null(Read(Crafted(4, NurseStep.MaxStage + 2, 0, 0)));
        Assert.NotNull(Read(Crafted(4, NurseStep.MaxStage + 1, 0, 0)));
        Assert.Null(Read(Crafted(NurseRosterData.MaxProcedure + 1, 0, 0, 0)));
        byte[] unknown = Encoded(new NurseStepsData { Steps = new[] { new NurseStep { Kind = NurseStepKind.Back } } });
        Assert.NotNull(Read(unknown));
        unknown[6] = 10; // the step's kind byte (after the header's 4 bytes and the step's 2-byte number)
        Assert.Null(Read(unknown));

        static byte[] Crafted(int procedure, int stageByte, int focus, int count)
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)procedure);
            writer.WriteByte((byte)stageByte);
            writer.WriteVarUInt((ulong)focus);
            writer.WriteVarUInt((ulong)count);
            return writer.ToArray();
        }
    }

    [Fact]
    public void An_attachment_of_steps_round_trips_and_is_only_steps()
    {
        MenuAttachment sent = MenuAttachment.NurseSteps(13, Sample());
        Assert.Equal((MenuAttachmentKind.NurseSteps, MenuAttachment.NurseStepsFormat), (sent.Kind, sent.Format));
        Assert.Contains("Nurse steps for #13", sent.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetNurseRoster(out _));
        var writer = new BitWriter();
        Assert.True(MenuAttachmentMessage.Write(writer, sent));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.True(read!.TryGetNurseSteps(out NurseStepsData? steps));
        Assert.Equal(Encoded(Sample()), Encoded(steps!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.NurseSteps(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.NurseSteps(1, null!));
    }

    [Fact]
    public void The_window_numbers_presses_and_settles_them_a_frame_later()
    {
        var window = new NurseStepWindow(procedure: 2, stage: 1, focus: 0);
        Assert.Empty(window.Settle(1, 0)); // nothing pressed, nothing moved
        window.Pressed(NurseStepKind.Direction, arg: 3);
        IReadOnlyList<NurseStep> added = window.Settle(1, 1);
        NurseStep step = Assert.Single(added);
        Assert.Equal((1, NurseStepKind.Direction, 3, 1, 1), (step.Number, step.Kind, step.Arg, step.Stage, step.Focus));

        // A held direction repeats: the focus moves with no press, a Focus step.
        step = Assert.Single(window.Settle(1, 2));
        Assert.Equal((2, NurseStepKind.Focus, 2), (step.Number, step.Kind, step.Arg));

        // The stage moves with no press (an automatic step): an Auto step.
        step = Assert.Single(window.Settle(3, 0));
        Assert.Equal((3, NurseStepKind.Auto, 3, 0), (step.Number, step.Kind, step.Stage, step.Focus));

        // Two presses in one frame both settle with what's there after; a focus of none isn't a Focus step.
        window.Pressed(NurseStepKind.Press, action: "UIDetails");
        window.Pressed(NurseStepKind.Confirm, reveal: true);
        added = window.Settle(12, -1);
        Assert.Equal(new[] { 4, 5 }, added.Select(s => s.Number));
        Assert.True(added[1].Reveal);
        Assert.Empty(window.Settle(12, -1));

        // An action that can't travel is left out, its number not used.
        window.Pressed(NurseStepKind.Press, action: "é");
        Assert.Empty(window.Settle(12, -1));
        Assert.Equal(5, window.Count);

        NurseStepsData data = window.Data();
        Assert.Equal((2, 1, 0, 5), (data.Procedure, data.StartStage, data.StartFocus, data.Newest));
        Assert.Null(data.Check());
    }

    [Theory]
    [InlineData(false, -1, false)] // the cards' warmed screen before the pick: a press there is no step
    [InlineData(true, -1, false)] // taken over, TakeOver not run yet: no procedure
    [InlineData(false, 7, false)] // a procedure left in a state whose event screen isn't taken over
    [InlineData(true, 0, true)] // the first procedure (Transplant) counts
    [InlineData(true, 7, true)] // heal all, at its result
    public void Steps_start_only_once_the_takeover_has_set_its_procedure(bool takenOver, int procedure, bool start) =>
        Assert.Equal(start, NurseStepWindow.CanStart(takenOver, procedure));

    [Fact]
    public void A_window_started_before_the_takeover_would_start_a_friend_at_the_wrong_stage()
    {
        // What the host sent when a press reached the warmed screen before the pick: no procedure (held to 0, Transplant)
        // and stage 0, which no procedure opens at (a friend's screen opens at the skater or, for heal all, at its result).
        NurseStepsData early = new NurseStepWindow(procedure: -1, stage: 0, focus: 0).Data();
        Assert.Equal((0, 0), (early.Procedure, early.StartStage));
        Assert.False(NurseStepWindow.CanStart(takenOver: false, procedure: -1));

        // Started at the takeover instead: heal all's own procedure and stage.
        NurseStepsData taken = new NurseStepWindow(procedure: 7, stage: 12, focus: 0).Data();
        Assert.Equal((7, 12), (taken.Procedure, taken.StartStage));
        Assert.Null(taken.Check());
    }

    [Fact]
    public void The_window_keeps_the_newest_steps_and_stops_at_the_last_number()
    {
        var window = new NurseStepWindow(1, 1, 0);
        for (int i = 0; i < 100; i++)
        {
            window.Pressed(NurseStepKind.Back);
            window.Settle(1, 0);
        }

        NurseStepsData data = window.Data();
        Assert.Equal(NurseStepsData.MaxSteps, data.Steps.Count);
        Assert.Equal((69, 100), (data.Steps[0].Number, data.Newest));
        Assert.Equal(Encoded(data), Encoded(Read(Encoded(data))!));

        // Arguments and stages out of range are held to it; 8 presses a frame at most.
        var clamped = new NurseStepWindow(99, 99, 999);
        Assert.Equal((NurseRosterData.MaxProcedure, NurseStep.MaxStage, NurseStep.MaxArg), (clamped.Procedure, clamped.StartStage, clamped.StartFocus));
        for (int i = 0; i < 20; i++)
        {
            clamped.Pressed(NurseStepKind.Direction, arg: 1000);
        }

        IReadOnlyList<NurseStep> added = clamped.Settle(70, -5);
        Assert.Equal(8, added.Count);
        Assert.All(added, s => Assert.Equal((NurseStep.MaxArg, NurseStep.MaxStage, -1), (s.Arg, s.Stage, s.Focus)));
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(Sample()), Encoded(new NurseStepsData { Procedure = 1, StartStage = 2 }) };
        var random = new Random(11);
        int read = 0;
        for (int round = 0; round < 6000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            if (round % 4 == 3)
            {
                bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);
            }
            else
            {
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!NurseStepsData.TryRead(ref reader, out NurseStepsData? steps))
            {
                Assert.Null(steps);
                continue;
            }

            read++;
            Assert.Null(steps!.Check());
            Assert.Equal(Encoded(steps), Encoded(Read(Encoded(steps))!));
            _ = steps.ToString();
        }

        Assert.True(read > 0, "some changed steps should still read");
    }
}
