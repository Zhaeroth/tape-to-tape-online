using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>A press made with Alt, Ctrl or Shift waits to see what it was for: Alt+Tab mustn't open the game's Settings.</summary>
public sealed class ModifierPressTests
{
    [Fact]
    public void It_waits_while_the_key_is_down()
    {
        Assert.Equal(ModifierStep.Wait, ModifierPress.Step(keyDown: true, otherKeyWentDown: false, focused: true));
    }

    [Fact]
    public void Let_go_alone_it_acts()
    {
        Assert.Equal(ModifierStep.Act, ModifierPress.Step(keyDown: false, otherKeyWentDown: false, focused: true));
    }

    [Fact]
    public void Alt_Tab_drops_it_by_the_tab()
    {
        Assert.Equal(ModifierStep.Drop, ModifierPress.Step(keyDown: true, otherKeyWentDown: true, focused: true));
    }

    [Fact]
    public void Alt_Tab_drops_it_by_the_focus_going_even_if_the_tab_was_never_seen()
    {
        Assert.Equal(ModifierStep.Drop, ModifierPress.Step(keyDown: true, otherKeyWentDown: false, focused: false));
    }

    [Fact]
    public void A_key_let_go_with_the_window_gone_is_no_press()
    {
        Assert.Equal(ModifierStep.Drop, ModifierPress.Step(keyDown: false, otherKeyWentDown: false, focused: false));
    }

    [Fact]
    public void Another_key_in_the_frame_it_was_let_go_drops_it()
    {
        Assert.Equal(ModifierStep.Drop, ModifierPress.Step(keyDown: false, otherKeyWentDown: true, focused: true));
    }
}

/// <summary>The check tour asks whether a return set the input lock: its screenshots can outlast the half second itself.</summary>
public sealed class InputLockWasSetTests
{
    [Fact]
    public void A_return_sets_it_and_the_sessions_end_clears_it()
    {
        var flow = new LobbyFlow();
        Assert.False(flow.InputLockWasSet);
        flow.Handle(LobbyEvent.HostPressed, 0);
        flow.Handle(LobbyEvent.LobbyCreated, 1);
        flow.Handle(LobbyEvent.SceneLeftTitle, 2);
        Assert.Equal(LobbyHostState.InGame, flow.Host);
        Assert.False(flow.InputLockWasSet);
        flow.Handle(LobbyEvent.TitleSceneLoaded, 3);
        flow.Handle(LobbyEvent.TitleReady, 4);
        Assert.Equal(LobbyHostState.Open, flow.Host);
        Assert.True(flow.InputLockWasSet);
        Assert.False(flow.IsInputLocked(4 + LobbyTiming.InputLockMicros + 1));
        Assert.True(flow.InputLockWasSet);
        flow.Handle(LobbyEvent.LeavePressed, 5);
        Assert.False(flow.InputLockWasSet);
    }
}

/// <summary>A line for the game's prompts, whose text has rich text on (a name holding a tag would be drawn as one; the tour's prompt_not_here step shows such a prompt).</summary>
public sealed class LobbyLineRichTextTests
{
    [Fact]
    public void A_names_tags_stay_text_in_every_language()
    {
        LobbyLine line = new LobbyLine(LobbyWords.AskLeave, "<color=red>x</color>").ForRichText();
        for (int language = 0; language < LobbyWords.LanguageCount; language++)
        {
            string text = line.Text(language);
            int open = text.IndexOf("<noparse>", System.StringComparison.Ordinal);
            int tag = text.IndexOf("<color=red>", System.StringComparison.Ordinal);
            int close = text.IndexOf("</noparse>", System.StringComparison.Ordinal);
            Assert.True(open >= 0 && open < tag && tag < close, text);
        }
    }

    [Fact]
    public void A_name_can_t_end_the_wrapping_early()
    {
        string text = new LobbyLine(LobbyWords.AskLeave, "a</noparse><size=500>b").ForRichText().Text(0);
        Assert.Equal(text.IndexOf("</noparse>", System.StringComparison.Ordinal), text.LastIndexOf("</noparse>", System.StringComparison.Ordinal));
        Assert.True(text.IndexOf("<size=500>", System.StringComparison.Ordinal) < text.IndexOf("</noparse>", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_line_with_no_arguments_is_the_same_text()
    {
        Assert.Equal(new LobbyLine(LobbyWords.AskCloseMany).Text(0), new LobbyLine(LobbyWords.AskCloseMany).ForRichText().Text(0));
    }
}
