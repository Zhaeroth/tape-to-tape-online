using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Core.Tests.Hooks;

/// <summary>
/// The hook lists (<c>src/T2TOnline.Plugin/Hooks/*.txt</c>) as the plugin reads them at start-up: a line its parsers refuse turns
/// online off for every player ("a start-up check failed"), and only the release gate (hookcheck.py) otherwise sees it before
/// then. The plugin's own parsers (<c>HookManifest</c>, <c>ClientModeList</c>) are linked into this project, so what's checked is
/// the real code on the real files, not a copy of its rules.
/// </summary>
public class HookListsTests
{
    private static readonly string[] Lists = { "hooks.txt", "menus_initdata.txt", "clientmode.txt" };
    private static readonly Regex TypeName = new(@"^[A-Za-z_][A-Za-z0-9_]*(`\d+)?(\.[A-Za-z_][A-Za-z0-9_]*(`\d+)?)*$", RegexOptions.CultureInvariant);
    private static readonly Regex MemberName = new(@"^(\.ctor|[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

    private static string Text(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hooks", file));

    private static List<HookEntry> Entries(string file) => HookManifest.Parse(Text(file), file).ToList();

    [Theory]
    [InlineData("hooks.txt")]
    [InlineData("menus_initdata.txt")]
    [InlineData("clientmode.txt")]
    public void A_list_parses_the_way_the_plugin_reads_it(string file) => Assert.NotEmpty(Entries(file));

    [Fact]
    public void The_client_mode_list_parses_with_its_sections()
    {
        IReadOnlyList<ClientModeRule> rules = ClientModeList.Parse(Text("clientmode.txt"));
        foreach (ClientModeAction action in new[] { ClientModeAction.Block, ClientModeAction.Disable, ClientModeAction.Save, ClientModeAction.Presentation })
        {
            Assert.Contains(rules, rule => rule.Action == action);
        }
    }

    [Fact]
    public void Every_row_is_a_type_and_a_member()
    {
        var problems = new List<string>();
        foreach (string file in Lists)
        {
            foreach (HookEntry entry in Entries(file))
            {
                if (!TypeName.IsMatch(entry.TypeName) || !MemberName.IsMatch(entry.MemberName))
                {
                    problems.Add($"{entry.Source}: {entry}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void A_method_has_one_decision_in_the_client_mode_list()
    {
        var seen = new Dictionary<string, ClientModeAction>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (ClientModeRule rule in ClientModeList.Parse(Text("clientmode.txt")))
        {
            string row = rule.Entry.ToString();
            if (seen.TryGetValue(row, out ClientModeAction before))
            {
                problems.Add($"{rule.Entry.Source}: {row} is {rule.Action} here and {before} above");
            }
            else
            {
                seen[row] = rule.Action;
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_menus_list_holds_the_menus_init_data_overrides_and_nothing_else()
    {
        List<HookEntry> entries = Entries("menus_initdata.txt");
        Assert.All(entries, entry => Assert.Equal("InitData", entry.MemberName));
        Assert.Equal(entries.Count, entries.Select(entry => entry.TypeName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_comment_or_a_section_decides_what_is_a_patch_target()
    {
        HookEntry[] rows = HookManifest.Parse(
            "A.B::Patched\nA.B::Read  # called only\nA.B::Icall  # interop only\n[disable]\nA.B::Folded\n[block]\nA.B::Blocked\n", "t").ToArray();
        Assert.Equal(new[] { true, false, false, false, true }, rows.Select(row => row.Patchable));
        Assert.Equal(new[] { "t:1", "t:2", "t:3", "t:5", "t:7" }, rows.Select(row => row.Source));
    }

    [Theory]
    [InlineData("NoSeparator")]
    [InlineData("::NoType")]
    [InlineData("A.B::")]
    public void A_row_without_a_type_and_a_member_is_refused(string line) =>
        Assert.Throws<FormatException>(() => HookManifest.Parse(line, "t").ToList());

    [Fact]
    public void A_bad_client_mode_row_is_named_by_its_line_in_the_file()
    {
        var bad = Assert.Throws<FormatException>(() => ClientModeList.Parse("# a comment\n[block]\nA.B::C\nno separator here\n"));
        Assert.StartsWith("clientmode.txt:4:", bad.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "clientmode.txt:3" }, ClientModeList.Parse("# a comment\n[block]\nA.B::C\n").Select(rule => rule.Entry.Source));
    }

    [Fact]
    public void The_client_mode_parser_refuses_an_unknown_section_and_an_entry_before_any()
    {
        Assert.Throws<FormatException>(() => ClientModeList.Parse("[blok]\nA.B::C\n"));
        Assert.Throws<FormatException>(() => ClientModeList.Parse("A.B::C\n"));
    }

    [Fact]
    public void The_presentation_list_has_its_sections_and_its_lines()
    {
        // PresentationList.Parse lives in the plugin's Streaming/EventChannel.cs, which needs the game: these are its rules.
        string[] sections = { "[listeners]", "[local]", "[menu]" };
        string section = string.Empty;
        var problems = new List<string>();
        int number = 0;
        foreach (string raw in Text("presentation.txt").Split('\n'))
        {
            number++;
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line;
                if (!sections.Contains(line))
                {
                    problems.Add($"presentation.txt:{number}: unknown section {line}");
                }

                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool ok = section switch
            {
                "[menu]" => parts.Length == 2 && parts[1] is "pause" or "resume" or "leave",
                "[listeners]" or "[local]" => parts.Length == 1,
                _ => false,
            };
            if (!ok)
            {
                problems.Add($"presentation.txt:{number}: {line}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_events_a_friends_form_and_frog_followers_raise_themselves_are_local()
    {
        // Match/CultistForms, Match/ZapForms and Match/FrogStatuses call the game's own TransformInElite, TransformBack,
        // _Transform, Revert, SetFrogged and RemoveFrogged on a friend's PC, and those raise these events there. Delivered
        // from the stream as well, a friend's sound and VFX listeners would play each twice, and the leak audit would count
        // every one as game logic running on the friend.
        var local = new HashSet<string>(StringComparer.Ordinal);
        string section = string.Empty;
        foreach (string raw in Text("presentation.txt").Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.StartsWith('['))
            {
                section = line;
            }
            else if (section == "[local]" && line.Length > 0)
            {
                local.Add(line);
            }
        }

        Assert.Subset(local, new HashSet<string>(StringComparer.Ordinal)
        {
            "RogueEvents.CultistTransformation",
            "RogueEvents.CultistTransformationBack",
            "RogueEvents.SkaterFrogged",
            "RogueEvents.SkaterUnfrogged",
        });
    }
}
