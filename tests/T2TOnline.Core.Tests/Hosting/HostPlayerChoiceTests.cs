using T2TOnline.Core.Hosting;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>Which tracked UI player is the host's own (D45).</summary>
public class HostPlayerChoiceTests
{
    [Fact]
    public void The_host_s_first_pad_player_wins_over_its_keyboard()
    {
        var choice = default(HostPlayerChoice);
        choice.Consider(0, friends: false, hasPad: false, hasKeyboardOrMouse: true);
        choice.Consider(1, friends: true, hasPad: false, hasKeyboardOrMouse: false);
        choice.Consider(2, friends: false, hasPad: true, hasKeyboardOrMouse: false);
        choice.Consider(3, friends: false, hasPad: true, hasKeyboardOrMouse: false);

        Assert.True(choice.HasPad);
        Assert.Equal(2, choice.Chosen);
    }

    [Fact]
    public void Without_a_pad_player_the_keyboard_player_decides()
    {
        var choice = default(HostPlayerChoice);
        choice.Consider(0, friends: true, hasPad: false, hasKeyboardOrMouse: false);
        choice.Consider(1, friends: false, hasPad: false, hasKeyboardOrMouse: true);
        choice.Consider(2, friends: false, hasPad: false, hasKeyboardOrMouse: true);

        Assert.False(choice.HasPad);
        Assert.Equal(1, choice.Chosen);
    }

    [Fact]
    public void A_friend_s_player_is_never_chosen()
    {
        var choice = default(HostPlayerChoice);
        choice.Consider(0, friends: true, hasPad: true, hasKeyboardOrMouse: true);
        choice.Consider(1, friends: false, hasPad: false, hasKeyboardOrMouse: false); // no controls at all

        Assert.Equal(-1, choice.Chosen);
        Assert.Equal(-1, default(HostPlayerChoice).Chosen);
    }
}
