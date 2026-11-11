using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Core.SettingsFiles;

namespace T2TOnline.Core.Tests.OnlineSettings;

/// <summary>
/// The players' build (D142): no text a player can read outside the words (a file in their folder, a bug report, a settings description, a
/// corner notice, the trigger line) names an F-key, a developer chord or the old panel. The words themselves are checked by
/// <c>SettingsWordsTests</c> with the same pattern.
/// </summary>
public class PlayerTextsTests
{
    // F-keys and the key chords of the developer tools, and the mod's old text panel.
    private static readonly Regex DeveloperThings = new(@"\bF\d{1,2}\b|Shift\s*\+|Ctrl\s*\+|\bpanel\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IEnumerable<object[]> PlayerTexts()
    {
        var texts = new List<(string Name, string Text)>
        {
            ("the game folder's README", DataFolderReadme.GameFolderText),
            ("the data folder's README", DataFolderReadme.DataFolderText),
            ("the shared folder's README", DataFolderReadme.SharedFolderText),
            ("the developer folder's notice", DataFolderReadme.DevNoticeText),
            ("a bug report's manifest", Manifest()),
            ("the players' mode line", PlayersBuild.ModeLine(devTools: false)),
            ("the NativeLobby line", PlayersBuild.NativeLobbyLine),
            ("the report advice (screens)", PlayersBuild.ReportAdvice(nativeScreens: true)),
            ("the report advice (no screens)", PlayersBuild.ReportAdvice(nativeScreens: false)),
            ("a friend's way out", PlayersBuild.Leave(devTools: false, host: false)),
            ("a host's way out", PlayersBuild.Leave(devTools: false, host: true)),
            ("without the lobby", PlayersBuild.WithoutLobby(devTools: false)),
            ("the offline notice's line (screens)", PlayersBuild.OfflineNoticeLine(nativeScreens: true, devTools: false)),
            ("the offline notice's line (no screens)", PlayersBuild.OfflineNoticeLine(nativeScreens: false, devTools: false)),
        };

        foreach (FieldInfo field in typeof(SettingDescriptions).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            texts.Add(($"settings.cfg's {field.Name}", (string)field.GetRawConstantValue()!));
        }

        foreach (GameVersionAge age in Enum.GetValues<GameVersionAge>())
        {
            texts.Add(($"the untested notice's reason ({age})", UntestedBuildNotice.Reason(age)));
            texts.Add(($"the untested notice's advice ({age})", UntestedBuildNotice.Advice(age, ProjectLinks.ReleasesShort)));
        }

        foreach (bool steamInput in new[] { false, true })
        {
            foreach (float peak in new[] { 0f, 0.3f })
            {
                texts.Add(($"the trigger line ({peak}, Steam Input {steamInput})", TriggerCheck.Notice("RT", peak, "LT", 1f, steamInput) ?? string.Empty));
                texts.Add(($"the trigger advice ({peak}, Steam Input {steamInput})", TriggerCheck.Advice("RT", peak, "LT", 1f, steamInput) ?? string.Empty));
            }
        }

        return texts.Select(t => new object[] { t.Name, t.Text });
    }

    [Theory]
    [MemberData(nameof(PlayerTexts))]
    public void No_player_text_names_a_developer_key_or_the_panel(string name, string text)
    {
        Assert.False(string.IsNullOrEmpty(text), name);
        Match found = DeveloperThings.Match(text);
        Assert.False(found.Success, $"{name} names \"{found.Value}\": {text}");
    }

    [Fact]
    public void The_pattern_catches_what_it_is_for()
    {
        foreach (string bad in new[] { "press F1, then Help", "F1 > Controller", "(F1, Backups)", "Ctrl+F6", "Shift+F7", "the mod's panel", "F7 leaves it" })
        {
            Assert.Matches(DeveloperThings, bad);
        }

        foreach (string good in new[] { "Settings > Online > Save Backups", "Marks (Insert):", "Steam build 26471720", "Online > Report a Problem", "F" })
        {
            Assert.DoesNotMatch(DeveloperThings, good);
        }
    }

    [Fact]
    public void The_trigger_line_gives_the_words_advice_not_a_key()
    {
        // The screen's notice_trigger and the log's line say the same thing: where a player can look at the trigger's range themselves.
        Assert.EndsWith("Check its range in Steam or in the pad's software.", TriggerCheck.Notice("RT", 0.3f, "LT", 1f, steamInput: false), StringComparison.Ordinal);
        Assert.EndsWith("Check its range in Steam or in the pad's software.", LobbyWords.NoticeTriggerShort.Texts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_names_the_mark_key_alone()
    {
        Assert.Contains("Marks (Insert):", Manifest(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_readmes_send_a_player_to_the_games_own_screens()
    {
        Assert.Contains("Settings > Online > Problem Report", DataFolderReadme.GameFolderText, StringComparison.Ordinal);
        Assert.Contains("Online > Report a Problem", DataFolderReadme.GameFolderText, StringComparison.Ordinal);
        Assert.Contains("Settings > Online > Save Backups", DataFolderReadme.DataFolderText, StringComparison.Ordinal);
        Assert.Contains("Settings > Online > Problem Report", DataFolderReadme.DataFolderText, StringComparison.Ordinal);
        Assert.Contains("Settings > Online > Save Backups", DataFolderReadme.SharedFolderText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_descriptions_name_the_native_homes()
    {
        Assert.Contains("lobby's Co-op Save", SettingDescriptions.CoopSlot, StringComparison.Ordinal);
        Assert.Contains(LobbyWords.CoopSave.Texts[0], SettingDescriptions.CoopSlot, StringComparison.Ordinal);
        Assert.StartsWith("Developer:", SettingDescriptions.NativeLobby, StringComparison.Ordinal);
        Assert.StartsWith("Developer:", SettingDescriptions.DevTools, StringComparison.Ordinal);
        Assert.Contains(PlayersBuild.PlayOnlineWith, SettingDescriptions.OnlineInput, StringComparison.Ordinal);
        Assert.Contains(PlayersBuild.SettingsOnline, SettingDescriptions.ShareAchievements, StringComparison.Ordinal);
        Assert.Contains(PlayersBuild.SettingsOnline, SettingDescriptions.ShareProgress, StringComparison.Ordinal);
    }

    private static string Manifest()
    {
        var facts = new BugReportFacts
        {
            MadeUtc = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
            Why = ReportSources.SettingsOnline,
            Release = "0.12.0-beta",
            Protocol = 9,
            GameBuild = "Nov 6 2026 build",
        };
        var result = new BugReportResult();
        result.Marks.Add("session-20261106T120000Z.log 12:00:01 UTC: MARK 1 (at the title)");
        return BugReportManifest.Text(facts, result);
    }
}
