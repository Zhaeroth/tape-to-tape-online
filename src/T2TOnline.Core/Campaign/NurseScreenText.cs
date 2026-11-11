using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The Nurse's procedure screen as text. Picking a Nurse card hands the event screen over to the game's own procedure screen (a
/// takeover with its own state, roster and input). A friend's game opens the same screen for real when it can
/// (<see cref="NurseFollower"/>); while the host's is up, the host's event menu data also carries what it shows (its stage, the
/// procedure, the focused option, its rows and other lines), and a friend's game that can't follow it shows that as text over
/// its real event screen. This is the data's layout (names in the menu data) and the friend's reading of it; the game glue fills
/// and draws it. Read only from data a friend got: never throws, and anything missing reads as nothing.
/// </summary>
public sealed class NurseScreenText
{
    /// <summary>Row group: one row of the procedure screen (a skater, a talent...), with <see cref="TextKey"/> and <see cref="SelectedKey"/>.</summary>
    public const string RowGroup = "nurse";

    /// <summary>Row group: another line of the procedure screen's text (a title, a prompt, a result), with <see cref="TextKey"/>.</summary>
    public const string LineGroup = "nurseline";

    /// <summary>Row value: the row's or line's text.</summary>
    public const string TextKey = "text";

    /// <summary>Row value: the row is the one highlighted on the host's screen (flag).</summary>
    public const string SelectedKey = "sel";

    /// <summary>Value: the screen's stage (the game's stage name, "Player"; "" when it has none).</summary>
    public const string StageKey = "nurse.stage";

    /// <summary>Value: the procedure being done (the game's procedure number; -1 for none).</summary>
    public const string ProcedureKey = "nurse.procedure";

    /// <summary>Value: the focused option (0-based; -1 for none).</summary>
    public const string IndexKey = "nurse.index";

    /// <summary>Value: the first option on screen (the screen scrolls a window over its options).</summary>
    public const string WindowKey = "nurse.window";

    /// <summary>Value: how many options the stage has.</summary>
    public const string OptionsKey = "nurse.options";

    /// <summary>Value: why the host's game couldn't read its procedure screen ("" when it could).</summary>
    public const string ProblemKey = "nurse.problem";

    /// <summary>Most rows sent.</summary>
    public const int MaxRows = 12;

    /// <summary>Most other lines sent.</summary>
    public const int MaxLines = 16;

    /// <summary>Longest stage name kept.</summary>
    public const int MaxStageChars = 32;

    /// <summary>Highest procedure, option or window number kept (larger ones read as none).</summary>
    public const int MaxNumber = 999;

    // The stages' meanings (NurseProcedureState.Stage), as the screen's prompt asks them.
    private static readonly Dictionary<string, string> StageWords = new(StringComparer.Ordinal)
    {
        ["Procedure"] = "choosing the procedure",
        ["Player"] = "choosing a skater",
        ["Talent"] = "choosing a talent",
        ["Recipient"] = "choosing who gets it",
        ["Attribute"] = "choosing a stat",
        ["SecondAttribute"] = "choosing a second stat",
        ["SecondPlayer"] = "choosing a second skater",
        ["SwapPreview"] = "looking at the swap",
        ["ExchangePreview"] = "looking at the exchange",
        ["AffectedPlayers"] = "looking at who it affects",
        ["TeamPreview"] = "looking at the team",
        ["Review"] = "reviewing the procedure",
        ["Result"] = "looking at the result",
        ["InjuryPreview"] = "looking at the injury",
        ["TransplantPreview"] = "looking at the transplant",
        ["SurgeryPreview"] = "looking at the surgery",
        ["SacrificePreview"] = "looking at the sacrifice",
        ["ClonePreview"] = "looking at the clone",
    };

    private NurseScreenText(string stage, int procedure, int index, int window, int options, string problem, IReadOnlyList<(string Text, bool Selected)> rows, IReadOnlyList<string> lines)
    {
        Stage = stage;
        Procedure = procedure;
        Index = index;
        Window = window;
        Options = options;
        Problem = problem;
        Rows = rows;
        Lines = lines;
    }

    /// <summary>The stage's game name ("Player"), or "".</summary>
    public string Stage { get; }

    /// <summary>The procedure's game number, or -1.</summary>
    public int Procedure { get; }

    /// <summary>The focused option (0-based), or -1.</summary>
    public int Index { get; }

    /// <summary>The first option on screen (0-based), or 0.</summary>
    public int Window { get; }

    /// <summary>How many options the stage has (0: none said).</summary>
    public int Options { get; }

    /// <summary>Why the host's game couldn't read its screen, or "".</summary>
    public string Problem { get; }

    /// <summary>The screen's rows, in order, with the host's highlight.</summary>
    public IReadOnlyList<(string Text, bool Selected)> Rows { get; }

    /// <summary>The screen's other lines, in order.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// The row the host has highlighted: the one marked selected; failing that, the focused option's place in the window
    /// when it's on screen; else -1.
    /// </summary>
    public int SelectedRow
    {
        get
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Selected)
                {
                    return i;
                }
            }

            int row = Index - Window;
            return Index >= 0 && row >= 0 && row < Rows.Count ? row : -1;
        }
    }

    /// <summary>The procedure screen as the host's menu data carries it, or null when it carries none (no takeover, or an older host).</summary>
    public static NurseScreenText? From(MenuData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.TryGet(StageKey, out _) && !data.TryGet(ProblemKey, out _))
        {
            return null;
        }

        var rows = new List<(string, bool)>();
        foreach (MenuItem row in data.Group(RowGroup).Take(MaxRows))
        {
            string text = row.Text(TextKey);
            if (text.Length > 0)
            {
                rows.Add((text, row.Flag(SelectedKey)));
            }
        }

        var lines = data.Group(LineGroup).Select(line => line.Text(TextKey)).Where(text => text.Length > 0).Take(MaxLines).ToList();
        return new NurseScreenText(
            SafeText.Clean(data.Text(StageKey), MaxStageChars),
            Number(data.Whole(ProcedureKey, -1), -1),
            Number(data.Whole(IndexKey, -1), -1),
            Number(data.Whole(WindowKey, 0), 0),
            Number(data.Whole(OptionsKey, 0), 0),
            data.Text(ProblemKey),
            rows,
            lines);
    }

    /// <summary>A stage in words ("choosing a skater"); an unknown one as its name in words; "" for none.</summary>
    public static string Words(string stage)
    {
        if (string.IsNullOrEmpty(stage) || stage == "Missing")
        {
            return string.Empty;
        }

        if (StageWords.TryGetValue(stage, out string? words))
        {
            return words;
        }

        // A stage a game update added: its name, split into lower-case words ("NewStage" -> "new stage").
        var split = new System.Text.StringBuilder(stage.Length + 8);
        for (int i = 0; i < stage.Length; i++)
        {
            char c = stage[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(stage[i - 1]))
            {
                split.Append(' ');
            }

            split.Append(char.ToLowerInvariant(c));
        }

        return SafeText.Clean(split.ToString(), MaxStageChars + 8);
    }

    /// <summary>
    /// The note over the text: <paramref name="why"/> (why it's text), then what the host is doing ("choosing a skater,
    /// option 2 of 5"), or why the host's game couldn't read it.
    /// </summary>
    public string Note(string why)
    {
        ArgumentNullException.ThrowIfNull(why);
        string lead = why.Length == 0 ? string.Empty : char.ToUpperInvariant(why[0]) + why[1..];
        if (Problem.Length > 0)
        {
            return $"{lead}{(lead.Length > 0 ? ": " : string.Empty)}the host's game couldn't read it ({Problem}).";
        }

        var parts = new List<string>(2);
        string doing = Words(Stage);
        if (doing.Length > 0)
        {
            parts.Add(doing);
        }

        if (Index >= 0 && Options > 0 && Index < Options)
        {
            parts.Add($"option {Index + 1} of {Options}");
        }

        string what = string.Join(", ", parts);
        return what.Length == 0 ? (lead.Length > 0 ? lead + "." : string.Empty)
            : lead.Length == 0 ? char.ToUpperInvariant(what[0]) + what[1..] + "."
            : $"{lead}: {what}.";
    }

    /// <summary>A short description for logs ("stage Player, option 2 of 5, 4 row(s), 3 line(s)").</summary>
    public override string ToString() =>
        Problem.Length > 0 ? $"unreadable ({Problem})"
        : $"stage {(Stage.Length > 0 ? Stage : "none")}, option {(Index >= 0 ? (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")} of {Options}, {Rows.Count} row(s), {Lines.Count} line(s)";

    private static int Number(long value, int none) => value is >= 0 and <= MaxNumber ? (int)value : none;
}
