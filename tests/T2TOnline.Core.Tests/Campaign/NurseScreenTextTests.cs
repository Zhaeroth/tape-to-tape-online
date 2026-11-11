using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NurseScreenTextTests
{
    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);

    private static MenuItem Row(string text, bool selected = false) => new(NurseScreenText.RowGroup, new[]
    {
        Value(NurseScreenText.TextKey, NetValue.FromString(text)),
        Value(NurseScreenText.SelectedKey, NetValue.FromBool(selected)),
    });

    private static MenuItem Line(string text) => new(NurseScreenText.LineGroup, new[] { Value(NurseScreenText.TextKey, NetValue.FromString(text)) });

    private static MenuData Data(IReadOnlyList<KeyValuePair<string, NetValue>> values, params MenuItem[] items) => new(values, items);

    private static List<KeyValuePair<string, NetValue>> Screen(string stage = "Player", int procedure = 0, int index = 1, int window = 0, int options = 4) => new()
    {
        Value("event.class", NetValue.FromString("NurseEvent")),
        Value(NurseScreenText.StageKey, NetValue.FromString(stage)),
        Value(NurseScreenText.ProcedureKey, NetValue.FromInt(procedure)),
        Value(NurseScreenText.IndexKey, NetValue.FromInt(index)),
        Value(NurseScreenText.WindowKey, NetValue.FromInt(window)),
        Value(NurseScreenText.OptionsKey, NetValue.FromInt(options)),
    };

    [Fact]
    public void The_screen_reads_back_from_the_menu_data()
    {
        MenuData data = Data(Screen(), Row("Jones · 72"), Row("Smith · 68", selected: true), Row(""), Line("Transplant"), Line("Pick a donor"),
            new MenuItem("choice", new[] { Value("text", NetValue.FromString("Not a nurse row")) }));
        NurseScreenText text = NurseScreenText.From(data)!;
        Assert.NotNull(text);
        Assert.Equal(("Player", 0, 1, 0, 4, string.Empty), (text.Stage, text.Procedure, text.Index, text.Window, text.Options, text.Problem));
        Assert.Equal(new[] { ("Jones · 72", false), ("Smith · 68", true) }, text.Rows);
        Assert.Equal(new[] { "Transplant", "Pick a donor" }, text.Lines);
        Assert.Equal(1, text.SelectedRow);
        Assert.Equal("stage Player, option 2 of 4, 2 row(s), 2 line(s)", text.ToString());
    }

    [Fact]
    public void Menu_data_without_the_screen_reads_as_none()
    {
        Assert.Null(NurseScreenText.From(MenuData.Empty));
        Assert.Null(NurseScreenText.From(Data(new List<KeyValuePair<string, NetValue>> { Value("event.class", NetValue.FromString("NurseEvent")) }, Line("x"))));
    }

    [Fact]
    public void The_highlight_falls_back_to_the_focused_option_in_the_window()
    {
        // No row marked: option 5 in a window from option 3 is the third row on screen.
        NurseScreenText text = NurseScreenText.From(Data(Screen(index: 4, window: 2, options: 9), Row("a"), Row("b"), Row("c"), Row("d")))!;
        Assert.Equal(2, text.SelectedRow);

        // Off screen, or no option: none.
        Assert.Equal(-1, NurseScreenText.From(Data(Screen(index: 9, window: 2, options: 12), Row("a"), Row("b")))!.SelectedRow);
        Assert.Equal(-1, NurseScreenText.From(Data(Screen(index: 1, window: 2, options: 12), Row("a"), Row("b")))!.SelectedRow);
        Assert.Equal(-1, NurseScreenText.From(Data(Screen(index: -1), Row("a")))!.SelectedRow);
    }

    [Fact]
    public void Numbers_out_of_range_read_as_none()
    {
        NurseScreenText text = NurseScreenText.From(Data(Screen(procedure: -7, index: 5000, window: -3, options: NurseScreenText.MaxNumber + 1)))!;
        Assert.Equal((-1, -1, 0, 0), (text.Procedure, text.Index, text.Window, text.Options));
        Assert.Equal("Choosing a skater.", text.Note(string.Empty));

        // The wrong kinds of value read as none too.
        var wrong = new List<KeyValuePair<string, NetValue>>
        {
            Value(NurseScreenText.StageKey, NetValue.FromInt(3)),
            Value(NurseScreenText.IndexKey, NetValue.FromString("2")),
        };
        NurseScreenText odd = NurseScreenText.From(Data(wrong))!;
        Assert.Equal((string.Empty, -1, -1), (odd.Stage, odd.Index, odd.Procedure));
    }

    [Fact]
    public void Rows_and_lines_are_capped()
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < 20; i++)
        {
            items.Add(Row($"row {i}"));
            items.Add(Line($"line {i}"));
        }

        NurseScreenText text = NurseScreenText.From(Data(Screen(), items.ToArray()))!;
        Assert.Equal((NurseScreenText.MaxRows, NurseScreenText.MaxLines), (text.Rows.Count, text.Lines.Count));
    }

    [Theory]
    [InlineData("Procedure", "choosing the procedure")]
    [InlineData("Player", "choosing a skater")]
    [InlineData("Talent", "choosing a talent")]
    [InlineData("Recipient", "choosing who gets it")]
    [InlineData("Attribute", "choosing a stat")]
    [InlineData("SecondAttribute", "choosing a second stat")]
    [InlineData("SecondPlayer", "choosing a second skater")]
    [InlineData("SwapPreview", "looking at the swap")]
    [InlineData("ExchangePreview", "looking at the exchange")]
    [InlineData("AffectedPlayers", "looking at who it affects")]
    [InlineData("TeamPreview", "looking at the team")]
    [InlineData("Review", "reviewing the procedure")]
    [InlineData("Result", "looking at the result")]
    [InlineData("InjuryPreview", "looking at the injury")]
    [InlineData("TransplantPreview", "looking at the transplant")]
    [InlineData("SurgeryPreview", "looking at the surgery")]
    [InlineData("SacrificePreview", "looking at the sacrifice")]
    [InlineData("ClonePreview", "looking at the clone")]
    [InlineData("NewStageKind", "new stage kind")]
    [InlineData("Missing", "")]
    [InlineData("", "")]
    public void Every_stage_has_words(string stage, string words) => Assert.Equal(words, NurseScreenText.Words(stage));

    [Fact]
    public void The_note_says_why_and_what_the_host_is_doing()
    {
        const string Why = "the Nurse's procedure screen shows as text in this version";
        NurseScreenText text = NurseScreenText.From(Data(Screen(stage: "Talent", index: 2, options: 3)))!;
        Assert.Equal("The Nurse's procedure screen shows as text in this version: choosing a talent, option 3 of 3.", text.Note(Why));
        Assert.Equal("Choosing a talent, option 3 of 3.", text.Note(string.Empty));

        // Nothing known about what it's doing: the reason alone.
        NurseScreenText bare = NurseScreenText.From(Data(Screen(stage: "Missing", index: -1, options: 0)))!;
        Assert.Equal("The Nurse's procedure screen shows as text in this version.", bare.Note(Why));
        Assert.Equal(string.Empty, bare.Note(string.Empty));
    }

    [Fact]
    public void A_screen_the_host_couldnt_read_says_why()
    {
        var values = new List<KeyValuePair<string, NetValue>> { Value(NurseScreenText.ProblemKey, NetValue.FromString("no procedure screen is up for it")) };
        NurseScreenText text = NurseScreenText.From(Data(values))!;
        Assert.Equal("no procedure screen is up for it", text.Problem);
        Assert.Equal("Shown as text: the host's game couldn't read it (no procedure screen is up for it).", text.Note("shown as text"));
        Assert.Equal("unreadable (no procedure screen is up for it)", text.ToString());
        Assert.Empty(text.Rows);
    }

    [Fact]
    public void The_screen_survives_the_wire()
    {
        MenuData data = Data(Screen(), Row("Jones · 72", selected: true), Line("Transplant"));
        var writer = new BitWriter();
        MenuEvent opened = MenuEvent.Opened(new MenuState(7, "EventChoice", "EventChoiceMenu", "world_map", 0, data));
        Assert.True(MenuMessages.Write(writer, opened));
        var reader = new BitReader(writer.ToArray());
        Assert.True(MenuMessages.TryRead(opened.Type, ref reader, out MenuEvent? read));
        NurseScreenText text = NurseScreenText.From(read!.State!.Data)!;
        Assert.Equal((0, "Jones · 72"), (text.SelectedRow, text.Rows[0].Text));
    }
}
