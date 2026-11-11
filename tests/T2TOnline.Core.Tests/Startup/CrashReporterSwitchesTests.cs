using System;
using System.Collections.Generic;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Core.Tests.Startup;

public class CrashReporterSwitchesTests
{
    [Fact]
    public void Both_on_are_switched_off_and_said_so()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10 };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions on -> off, CrashReportHandler.logBufferSize 10 -> 0", outcome.Text);
        Assert.True(outcome.Changed);
        Assert.False(outcome.StillOn);
        Assert.False(outcome.NoReporter);
        Assert.Equal((byte)0, engine.Capture);
        Assert.Equal(0u, engine.Buffer);
    }

    [Fact]
    public void Switches_already_off_are_not_written()
    {
        var engine = new FakeEngine { Capture = 0, Buffer = 0 };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions off, CrashReportHandler.logBufferSize 0", outcome.Text);
        Assert.False(outcome.Changed);
        Assert.False(outcome.StillOn);
        Assert.Empty(engine.Writes);
    }

    [Fact]
    public void Only_the_switch_that_is_on_is_written()
    {
        var engine = new FakeEngine { Capture = 0, Buffer = 10 };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions off, CrashReportHandler.logBufferSize 10 -> 0", outcome.Text);
        Assert.Equal(new[] { "buffer 0" }, engine.Writes);
    }

    [Fact]
    public void A_write_that_does_not_stick_is_still_on()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, IgnoreWrites = true };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions on -> on, CrashReportHandler.logBufferSize 10 -> 10", outcome.Text);
        Assert.True(outcome.StillOn);
    }

    [Fact]
    public void Without_getters_nothing_is_written()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, HasGetters = false };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions unavailable, CrashReportHandler.logBufferSize unavailable", outcome.Text);
        Assert.False(outcome.Changed);
        Assert.False(outcome.StillOn);
        Assert.Empty(engine.Writes);
    }

    [Fact]
    public void Without_setters_the_switches_stay_on_and_say_why()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, HasSetters = false };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions on -> on (no setter in this engine), " +
            "CrashReportHandler.logBufferSize 10 -> 10 (no setter in this engine)", outcome.Text);
        Assert.False(outcome.Changed);
        Assert.True(outcome.StillOn);
    }

    [Fact]
    public void Engine_calls_that_throw_never_get_out()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, ThrowOnWrite = true };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions on -> on (couldn't set it: InvalidOperationException), " +
            "CrashReportHandler.logBufferSize 10 -> 10 (couldn't set it: InvalidOperationException)", outcome.Text);
        Assert.True(outcome.StillOn);

        engine = new FakeEngine { Capture = 1, Buffer = 10, ThrowOnRead = true };
        outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions unavailable (InvalidOperationException), " +
            "CrashReportHandler.logBufferSize unavailable (InvalidOperationException)", outcome.Text);
        Assert.Empty(engine.Writes);
    }

    [Fact]
    public void A_read_back_that_fails_counts_as_still_on()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, ThrowOnReadAfterWrite = true };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        // The capture's write came first, so the buffer can't be read at all after it.
        Assert.Equal("CrashReportHandler.enableCaptureExceptions on -> unavailable (InvalidOperationException), " +
            "CrashReportHandler.logBufferSize unavailable (InvalidOperationException)", outcome.Text);
        Assert.True(outcome.Changed);
        Assert.True(outcome.StillOn);
    }

    [Fact]
    public void Without_the_reporter_nothing_is_called()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 10, Exists = false };
        CrashReporterOutcome outcome = CrashReporterSwitches.SwitchOff(engine);
        Assert.True(outcome.NoReporter);
        Assert.False(outcome.StillOn);
        Assert.Equal("CrashReportHandler.enableCaptureExceptions and CrashReportHandler.logBufferSize not touched (the crash reporter doesn't exist yet)", outcome.Text);
        Assert.Equal(0, engine.Calls);
        Assert.False(CrashReporterSwitches.AnyOn(engine));
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public void The_buffer_is_only_ever_written_zero()
    {
        var engine = new FakeEngine { Capture = 1, Buffer = 50 };
        CrashReporterSwitches.SwitchOff(engine);
        Assert.Equal(new[] { "capture 0", "buffer 0" }, engine.Writes);
    }

    [Theory]
    [InlineData(0, 0u, false)]
    [InlineData(1, 0u, true)]
    [InlineData(0, 10u, true)]
    [InlineData(1, 10u, true)]
    public void AnyOn_reads_both_switches(byte capture, uint buffer, bool expected)
    {
        var engine = new FakeEngine { Capture = capture, Buffer = buffer };
        Assert.Equal(expected, CrashReporterSwitches.AnyOn(engine));
        Assert.Empty(engine.Writes);
    }

    [Fact]
    public void AnyOn_is_false_without_getters()
    {
        Assert.False(CrashReporterSwitches.AnyOn(new FakeEngine { Capture = 1, Buffer = 10, HasGetters = false }));
    }

    [Fact]
    public void AnyOn_lets_an_engine_failure_through_for_the_caller()
    {
        Assert.Throws<InvalidOperationException>(() => CrashReporterSwitches.AnyOn(new FakeEngine { Capture = 0, ThrowOnRead = true }));
    }

    private sealed class FakeEngine : ICrashReporterEngine
    {
        private bool _written;

        public byte Capture { get; set; }

        public uint Buffer { get; set; }

        public bool Exists { get; set; } = true;

        public bool HasGetters { get; set; } = true;

        public bool HasSetters { get; set; } = true;

        public bool IgnoreWrites { get; set; }

        public bool ThrowOnRead { get; set; }

        public bool ThrowOnReadAfterWrite { get; set; }

        public bool ThrowOnWrite { get; set; }

        public int Calls { get; private set; }

        public List<string> Writes { get; } = new();

        public bool ReporterExists => Exists;

        public byte? ReadCapture()
        {
            Read();
            return HasGetters ? Capture : null;
        }

        public bool WriteCapture(byte value)
        {
            if (!Write())
            {
                return false;
            }

            Writes.Add($"capture {value}");
            if (!IgnoreWrites)
            {
                Capture = value;
            }

            return true;
        }

        public uint? ReadLogBufferSize()
        {
            Read();
            return HasGetters ? Buffer : null;
        }

        public bool WriteLogBufferSize(uint value)
        {
            if (!Write())
            {
                return false;
            }

            Writes.Add($"buffer {value}");
            if (!IgnoreWrites)
            {
                Buffer = value;
            }

            return true;
        }

        private void Read()
        {
            Calls++;
            if (ThrowOnRead || (ThrowOnReadAfterWrite && _written))
            {
                throw new InvalidOperationException("engine read failed");
            }
        }

        private bool Write()
        {
            Calls++;
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("engine write failed");
            }

            if (!HasSetters)
            {
                return false;
            }

            _written = true;
            return true;
        }
    }
}
