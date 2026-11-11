using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Tests.Input;

public class GamepadInputTests
{
    [Fact]
    public void Every_pad_input_has_a_distinct_template_element()
    {
        var ids = Enumerable.Range(0, PadState.AxisCount).Select(a => GamepadTemplateLayout.AxisElementId((PadAxis)a))
            .Concat(Enumerable.Range(0, PadState.ButtonCount).Select(GamepadTemplateLayout.ButtonElementId))
            .ToList();
        Assert.Equal(PadState.AxisCount + PadState.ButtonCount, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(4, false, 0)] // A
    [InlineData(20, false, 13)] // d-pad right is the last PadButtons bit
    [InlineData(21, false, 11)] // d-pad down
    [InlineData(11, true, 4)] // left trigger
    [InlineData(3, true, 3)] // right stick Y
    public void Template_elements_map_back_to_pad_inputs(int elementId, bool isAxis, int index)
    {
        Assert.True(GamepadTemplateLayout.TryGetPadInput(elementId, out bool axis, out int i));
        Assert.Equal(isAxis, axis);
        Assert.Equal(index, i);
    }

    [Theory]
    [InlineData(6)] // third face-button row
    [InlineData(16)] // guide
    [InlineData(23)] // whole left stick
    [InlineData(25)] // whole d-pad
    public void Elements_a_standard_pad_lacks_are_not_mapped(int elementId) =>
        Assert.False(GamepadTemplateLayout.TryGetPadInput(elementId, out _, out _));

    [Fact]
    public void Template_elements_have_names_for_the_log_and_say_whether_they_are_carried()
    {
        Assert.Equal("RightTrigger", GamepadTemplateLayout.ElementName(13));
        Assert.StartsWith("Guide", GamepadTemplateLayout.ElementName(16));
        Assert.Equal("element 31", GamepadTemplateLayout.ElementName(31));
        Assert.Equal("element -1", GamepadTemplateLayout.ElementName(-1));
        Assert.True(GamepadTemplateLayout.Carries(13));
        Assert.False(GamepadTemplateLayout.Carries(16));
        for (int i = 0; i < PadState.AxisCount; i++)
        {
            Assert.Equal(((PadAxis)i).ToString(), GamepadTemplateLayout.ElementName(GamepadTemplateLayout.AxisElementId((PadAxis)i)));
        }

        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            string name = GamepadTemplateLayout.ElementName(GamepadTemplateLayout.ButtonElementId(i));
            Assert.True(GamepadTemplateLayout.Carries(GamepadTemplateLayout.ButtonElementId(i)), name);
        }
    }

    [Fact]
    public void Round_trips_every_pad_input()
    {
        for (int b = 0; b < PadState.ButtonCount; b++)
        {
            Assert.True(GamepadTemplateLayout.TryGetPadInput(GamepadTemplateLayout.ButtonElementId(b), out bool axis, out int i));
            Assert.False(axis);
            Assert.Equal(b, i);
        }

        for (int a = 0; a < PadState.AxisCount; a++)
        {
            Assert.True(GamepadTemplateLayout.TryGetPadInput(GamepadTemplateLayout.AxisElementId((PadAxis)a), out bool axis, out int i));
            Assert.True(axis);
            Assert.Equal(a, i);
        }
    }

    [Fact]
    public void Hold_tracker_reports_each_release_with_its_duration()
    {
        var tracker = new HoldTracker();
        var done = new List<CompletedHold>();
        tracker.Observe(100, PadButtons.A, done);
        tracker.Observe(150, PadButtons.A | PadButtons.X, done);
        tracker.Observe(400, PadButtons.X, done);
        tracker.Observe(500, PadButtons.None, done);

        Assert.Equal(new[] { new CompletedHold(PadButtons.A, 100, 300), new CompletedHold(PadButtons.X, 150, 350) }, done);
    }

    [Fact]
    public void Hold_tracker_ignores_unchanged_observations()
    {
        var tracker = new HoldTracker();
        var done = new List<CompletedHold>();
        tracker.Observe(0, PadButtons.B, done);
        tracker.Observe(10, PadButtons.B, done);
        tracker.Observe(20, PadButtons.B, done);
        Assert.Empty(done);
        tracker.Observe(30, PadButtons.None, done);
        Assert.Equal(30, Assert.Single(done).Duration);
    }
}
