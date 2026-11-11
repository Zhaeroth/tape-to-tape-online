using T2TOnline.Core.Hosting;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>A friend's player asking for the host's menus when nobody controls them (D45).</summary>
public class FriendControlAskTests
{
    private static HostPlayerChoice Host(bool pad, bool keyboard)
    {
        var choice = default(HostPlayerChoice);
        choice.Consider(0, friends: false, hasPad: false, hasKeyboardOrMouse: keyboard);
        choice.Consider(1, friends: true, hasPad: false, hasKeyboardOrMouse: false); // a friend's virtual pad
        if (pad)
        {
            choice.Consider(2, friends: false, hasPad: true, hasKeyboardOrMouse: false);
        }

        return choice;
    }

    [Fact]
    public void A_keyboard_host_refuses_a_friend_s_ask()
    {
        // The game never lists a keyboard player as controlling, so its own answer would be yes at every press.
        Assert.True(FriendControlAsk.Refused(hosting: true, friendsPlayer: true, Host(pad: false, keyboard: true)));
    }

    [Fact]
    public void A_host_with_a_pad_player_leaves_the_ask_to_the_game()
    {
        // The game hands the menus over once, to the host's pad (the mod's swap), and refuses the friend from then on.
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: true, Host(pad: true, keyboard: true)));
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: true, Host(pad: true, keyboard: false)));
    }

    [Fact]
    public void The_host_s_own_players_are_never_refused()
    {
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: false, Host(pad: false, keyboard: true)));
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: false, Host(pad: true, keyboard: true)));
    }

    [Fact]
    public void Outside_hosting_nothing_is_refused()
    {
        // Couch play, and a friend's own game: the game's answer stands.
        Assert.False(FriendControlAsk.Refused(hosting: false, friendsPlayer: true, Host(pad: false, keyboard: true)));
        Assert.False(FriendControlAsk.Refused(hosting: false, friendsPlayer: false, Host(pad: false, keyboard: true)));
    }

    [Fact]
    public void With_no_host_player_at_all_the_game_s_answer_stands()
    {
        // Nobody of the host's could take the menus: they stay with whoever asked, as the swap leaves them too.
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: true, Host(pad: false, keyboard: false)));
        Assert.False(FriendControlAsk.Refused(hosting: true, friendsPlayer: true, default));
    }
}
