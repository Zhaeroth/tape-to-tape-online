using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Core.Tests.Startup;

/// <summary>
/// The install check's rules (<c>src/T2TOnline.Plugin/Startup/InstallLayout.cs</c>, linked into this project: the plugin runs
/// them before it may touch Core) on an in-memory game folder: the zip's layout, an update over 0.10.0's flat files, a second
/// copy, a mixed build, the flat layout alone, and MelonLoader's folder settings.
/// </summary>
public class InstallLayoutTests
{
    private const string New = "0.11.0-beta+0123456789abcdef0123456789abcdef01234567";
    private const string Other = "0.11.0-beta+fedcba9876543210fedcba9876543210fedcba98";
    private const string Old = "0.10.0-beta+89abcdef0123456789abcdef0123456789abcdef";
    private const string Home = @"Mods\T2TOnline";

    private static readonly Version V11 = new(0, 11, 0, 0);
    private static readonly Version V10 = new(0, 10, 0, 0);

    [Fact]
    public void The_zips_layout_alone_is_clean()
    {
        FakeTree tree = NewLayout(new FakeTree());

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(InstallOutcome.Clean, verdict.Outcome);
        Assert.Empty(verdict.Entries);
        Assert.False(verdict.Flat);
        Assert.Equal(@"Mods\T2TOnline\Plugins", verdict.LoadedFrom);
        Assert.Equal(@"Install check: clean: T2T Online 0.11.0-beta and its two libraries are one build, loaded from Mods\T2TOnline\Plugins; no other copies.",
            InstallLayout.LogLine(verdict, "0.11.0-beta"));
    }

    [Fact]
    public void An_update_over_the_flat_files_of_0_10_0_names_all_six()
    {
        FakeTree tree = OldFlat(NewLayout(new FakeTree()));

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(InstallOutcome.Leftovers, verdict.Outcome);
        Assert.Equal(
            new[] { @"Plugins\T2TOnline.dll", @"Plugins\T2TOnline.pdb", @"UserLibs\T2TOnline.Core.dll", @"UserLibs\T2TOnline.Core.pdb", @"UserLibs\T2TOnline.Steam.dll", @"UserLibs\T2TOnline.Steam.pdb" },
            verdict.Entries.Select(e => e.Path));
        Assert.Equal(verdict.Entries.Select(e => e.Path), verdict.Entries.Select(e => e.ForLog));
        string line = InstallLayout.LogLine(verdict, "0.11.0-beta");
        Assert.StartsWith(@"Install check: old files, online is off this session: 6 to delete: Plugins\T2TOnline.dll, Plugins\T2TOnline.pdb, ", line, StringComparison.Ordinal);
        Assert.EndsWith(@"T2T Online 0.11.0-beta itself is whole and loaded from Mods\T2TOnline\Plugins.", line, StringComparison.Ordinal);
        Assert.DoesNotContain(tree.Root, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_root_documents_of_0_10_0_are_found_by_their_content_with_either_line_ending()
    {
        byte[] license = Encoding.UTF8.GetBytes("MIT License\n\nCopyright (c) 2026 Alex\n\nPermission is hereby granted.\n");
        byte[] notices = Encoding.UTF8.GetBytes("# Third-party notices\n\nT2T Online ships no third-party code.\n");
        var known = new Dictionary<string, string>
        {
            ["LICENSE"] = InstallLayout.HashWithoutCarriageReturns(license),
            ["THIRD_PARTY_NOTICES.md"] = InstallLayout.HashWithoutCarriageReturns(notices),
        };
        FakeTree tree = NewLayout(new FakeTree())
            .Add("LICENSE", Encoding.UTF8.GetBytes("MIT License\r\n\r\nCopyright (c) 2026 Alex\r\n\r\nPermission is hereby granted.\r\n"))
            .Add("THIRD_PARTY_NOTICES.md", notices)
            .Add("T2T Online - READ ME FIRST.txt");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree), known);

        Assert.Equal(InstallOutcome.Leftovers, verdict.Outcome);
        Assert.Equal(new[] { "LICENSE", "THIRD_PARTY_NOTICES.md" }, verdict.Entries.Select(e => e.Path));
    }

    [Fact]
    public void The_files_in_folders_come_first_and_the_game_folders_own_last()
    {
        byte[] license = Encoding.UTF8.GetBytes("MIT License\n");
        FakeTree tree = OldFlat(NewLayout(new FakeTree())).Add("LICENSE", license).Add("T2T Online - INSTALL.txt");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree), new Dictionary<string, string> { ["LICENSE"] = InstallLayout.HashWithoutCarriageReturns(license) });

        Assert.Equal(8, verdict.Entries.Count);
        Assert.Equal(@"Plugins\T2TOnline.dll", verdict.Entries[0].Path);
        Assert.Equal(new[] { "LICENSE", "T2T Online - INSTALL.txt" }, verdict.Entries.Skip(6).Select(e => e.Path));
    }

    [Fact]
    public void Another_mods_license_at_the_root_is_left_alone()
    {
        FakeTree tree = NewLayout(new FakeTree()).Add("LICENSE", Encoding.UTF8.GetBytes("Apache License, Version 2.0\n")).Add("THIRD_PARTY_NOTICES.md", Encoding.UTF8.GetBytes("# Notices of another mod\n"));

        Assert.Equal(InstallOutcome.Clean, InstallLayout.Decide(tree, Facts(tree)).Outcome);
    }

    [Fact]
    public void The_known_root_documents_are_0_10_0s_two_by_name_and_sha_256()
    {
        Assert.Equal(new[] { "LICENSE", "THIRD_PARTY_NOTICES.md" }, InstallLayout.OldRootDocumentHashes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(InstallLayout.OldRootDocumentHashes.Values, hash => Assert.Matches("^[0-9a-f]{64}$", hash));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", InstallLayout.HashWithoutCarriageReturns(Encoding.ASCII.GetBytes("\r\r")));
    }

    [Fact]
    public void The_first_releases_root_files_are_found_by_name()
    {
        FakeTree tree = NewLayout(new FakeTree()).Add("T2T Online - Install.cmd").Add("T2T Online - INSTALL.txt");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(new[] { "T2T Online - Install.cmd", "T2T Online - INSTALL.txt" }, verdict.Entries.Select(e => e.Path));
    }

    [Theory]
    [InlineData(@"Mods\T2TOnline-copy")]
    [InlineData(@"Plugins\T2TOnline")]
    public void A_second_copy_in_another_melon_folder_is_named_as_one_folder(string copy)
    {
        FakeTree tree = NewLayout(NewLayout(new FakeTree(), copy, Old, V10));

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(InstallOutcome.Leftovers, verdict.Outcome);
        InstallEntry entry = Assert.Single(verdict.Entries);
        Assert.Equal(copy + "\\", entry.Path);
        Assert.Equal(copy.Split('\\')[1] == "T2TOnline" ? copy + "\\" : @"Mods\[folder 1]\", entry.ForLog);
    }

    [Fact]
    public void A_second_copy_of_the_same_build_that_wasnt_loaded_is_a_leftover_too()
    {
        // MelonLoader keeps the first of two equal versions: the plugin here came from Mods\T2TOnline.
        FakeTree tree = NewLayout(NewLayout(new FakeTree()), @"Mods\T2TOnline (2)");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(new[] { @"Mods\T2TOnline (2)\" }, verdict.Entries.Select(e => e.Path));
    }

    [Fact]
    public void A_copy_sharing_a_folder_with_another_mod_is_named_file_by_file()
    {
        FakeTree tree = NewLayout(NewLayout(new FakeTree()), @"Mods\Pack", Old, V10).Dll(@"Mods\Pack\UserLibs\OtherLib.dll", "OtherLib", new Version(1, 0), "1.0");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(6, verdict.Entries.Count);
        Assert.All(verdict.Entries, e => Assert.StartsWith(@"Mods\Pack\", e.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(verdict.Entries, e => e.Path.Contains("OtherLib", StringComparison.Ordinal));
        Assert.All(verdict.Entries, e => Assert.StartsWith(@"Mods\[folder 1]\", e.ForLog, StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_under_another_file_name_is_found_by_its_assembly_name_and_the_log_doesnt_write_that_name()
    {
        FakeTree tree = NewLayout(new FakeTree()).Dll(@"Plugins\T2TOnline (1).dll", InstallLayout.PluginName, V10, Old).Dll(@"Plugins\T2TOnlineExtras.dll", "T2TOnlineExtras", V10, "1.0");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        InstallEntry entry = Assert.Single(verdict.Entries);
        Assert.Equal(@"Plugins\T2TOnline (1).dll", entry.Path);
        Assert.Equal(@"Plugins\[a copy of T2TOnline.dll]", entry.ForLog);
    }

    [Fact]
    public void A_library_in_a_plugins_folder_is_never_loaded_so_its_left_over()
    {
        FakeTree tree = NewLayout(new FakeTree()).Dll(@"Plugins\T2TOnline.Core.dll", InstallLayout.CoreName, V11, New);

        Assert.Equal(new[] { @"Plugins\T2TOnline.Core.dll" }, InstallLayout.Decide(tree, Facts(tree)).Entries.Select(e => e.Path));
    }

    [Fact]
    public void Of_two_equal_libraries_the_one_beside_the_plugin_is_kept()
    {
        // MelonLoader loaded the flat copy (the root folder comes first on a tie); the zip's folder keeps its own.
        FakeTree tree = NewLayout(new FakeTree()).Dll(@"UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V11, New);

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, core: @"UserLibs\T2TOnline.Core.dll"));

        Assert.Equal(InstallOutcome.Leftovers, verdict.Outcome);
        Assert.Equal(new[] { @"UserLibs\T2TOnline.Core.dll" }, verdict.Entries.Select(e => e.Path));
    }

    [Fact]
    public void A_library_of_another_build_is_a_mixed_install_naming_its_file()
    {
        FakeTree tree = NewLayout(new FakeTree()).Dll(@"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V11, Other);

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, coreBuild: Other));

        Assert.Equal(InstallOutcome.Mixed, verdict.Outcome);
        Assert.Equal(new[] { @"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll", @"Mods\T2TOnline\UserLibs\T2TOnline.Core.pdb" }, verdict.Entries.Select(e => e.Path));
        Assert.Equal($@"T2TOnline.Core is {Other}, the plugin {New} (Mods\T2TOnline\UserLibs\T2TOnline.Core.dll)", Assert.Single(verdict.Problems));
        string line = InstallLayout.LogLine(verdict, "0.11.0-beta");
        Assert.StartsWith("Install check: mixed: T2TOnline.Core is ", line, StringComparison.Ordinal);
        Assert.Contains("is off this session: no patches, nothing online.", line, StringComparison.Ordinal);
        Assert.Contains(@"Files of another build: Mods\T2TOnline\UserLibs\T2TOnline.Core.dll, Mods\T2TOnline\UserLibs\T2TOnline.Core.pdb.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_library_copied_over_the_new_build_is_mixed_and_older()
    {
        FakeTree tree = NewLayout(new FakeTree()).Dll(@"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V10, Old);

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, coreBuild: Old, coreVersion: V10));

        Assert.Equal(InstallOutcome.Mixed, verdict.Outcome);
        Assert.StartsWith($"T2TOnline.Core {Old} is older than the 0.11.0.0 the plugin needs", Assert.Single(verdict.Problems), StringComparison.Ordinal);
        IReadOnlyList<string> notice = InstallLayout.MixedNotice(verdict);
        Assert.Equal("T2T Online is off: part of it is from another version, so the game runs without it.", notice[0]);
        Assert.Contains(@"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll", notice[1], StringComparison.Ordinal);
        Assert.Contains("T2T Online - READ ME FIRST.txt", notice[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_library_or_a_steam_assembly_of_another_build_is_mixed_too()
    {
        FakeTree tree = NewLayout(new FakeTree());
        InstallFacts facts = Facts(tree);

        InstallVerdict missing = InstallLayout.Decide(tree, facts with { Core = null });
        InstallVerdict steam = InstallLayout.Decide(tree, facts with { Steam = facts.Steam! with { Build = Other } });

        Assert.Equal(InstallOutcome.Mixed, missing.Outcome);
        Assert.Equal("T2TOnline.Core isn't loaded (its DLL is missing, or MelonLoader couldn't load it)", Assert.Single(missing.Problems));
        Assert.Equal(InstallOutcome.Mixed, steam.Outcome);
        Assert.StartsWith("T2TOnline.Steam is ", Assert.Single(steam.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mixed_install_with_old_flat_files_names_every_file_of_another_build()
    {
        // 0.10.0's flat Core is found first and kept by MelonLoader on a tie with an old copy in the zip's folder.
        FakeTree tree = OldFlat(NewLayout(new FakeTree())).Dll(@"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V10, Old);

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, core: @"UserLibs\T2TOnline.Core.dll", coreBuild: Old, coreVersion: V10));

        Assert.Equal(InstallOutcome.Mixed, verdict.Outcome);
        Assert.Contains(verdict.Entries, e => e.Path == @"Plugins\T2TOnline.dll");
        Assert.Contains(verdict.Entries, e => e.Path == @"UserLibs\T2TOnline.Core.dll");
        Assert.Contains(verdict.Entries, e => e.Path == @"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll");
        Assert.DoesNotContain(verdict.Entries, e => e.Path == @"Mods\T2TOnline\Plugins\T2TOnline.dll");
    }

    [Fact]
    public void The_flat_layout_alone_works_and_the_log_says_how_the_zip_installs()
    {
        FakeTree tree = NewFlat(new FakeTree());

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, @"Plugins\T2TOnline.dll", @"UserLibs\T2TOnline.Core.dll", @"UserLibs\T2TOnline.Steam.dll"));

        Assert.Equal(InstallOutcome.Clean, verdict.Outcome);
        Assert.True(verdict.Flat);
        Assert.Contains(@"loaded from Plugins; no other copies. The flat folders (Plugins and UserLibs) work; the zip installs as Mods\T2TOnline.", InstallLayout.LogLine(verdict, "0.11.0-beta"), StringComparison.Ordinal);
    }

    [Fact]
    public void With_melon_folders_off_the_zips_folder_isnt_loaded_and_is_named()
    {
        FakeTree tree = NewLayout(NewFlat(new FakeTree()));

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree, @"Plugins\T2TOnline.dll", @"UserLibs\T2TOnline.Core.dll", @"UserLibs\T2TOnline.Steam.dll") with { SubfoldersOff = true });

        Assert.Equal(InstallOutcome.Leftovers, verdict.Outcome);
        Assert.True(verdict.SubfoldersOff);
        Assert.Equal(new[] { @"Mods\T2TOnline\" }, verdict.Entries.Select(e => e.Path));
        Assert.Contains("disable_subfolder_load", InstallLayout.LogLine(verdict, "0.11.0-beta"), StringComparison.Ordinal);
        Assert.All(InstallLayout.Folders(tree, tree.Root, subfoldersOff: true, manifestNotNeeded: false, out _).Where(f => f.Top != null), f => Assert.False(f.Scanned));
    }

    [Fact]
    public void Melon_folders_follow_melonloaders_rules()
    {
        FakeTree tree = new FakeTree()
            .Add(@"Mods\A\manifest.json").Add(@"Mods\A\Plugins\x.txt").Add(@"Mods\A\UserLibs\x.txt").Add(@"Mods\A\Extra\Mods\x.txt")
            .Add(@"Mods\NoManifest\x.txt")
            .Add(@"Mods\~Old\manifest.json").Add(@"Mods\.hidden\manifest.json").Add(@"Mods\Disabled\manifest.json").Add(@"Mods\Broken\manifest.json").Add(@"Mods\Retired\manifest.json")
            .Add(@"UserLibs\B\manifest.json").Add(@"UserLibs\B\Plugins\x.txt")
            .Add(@"Plugins\C\manifest.json").Add(@"Plugins\C\Mods\x.txt").Add(@"Plugins\C\Plugins\x.txt");

        List<(string, MelonPhase, string?)> folders = InstallLayout.Folders(tree, tree.Root, false, false, out int unreadable)
            .Select(f => (Path.GetRelativePath(tree.Root, f.Path).Replace(Path.DirectorySeparatorChar, '\\'), f.Phase, f.Top == null ? null : Path.GetRelativePath(tree.Root, f.Top).Replace(Path.DirectorySeparatorChar, '\\')))
            .ToList();

        Assert.Equal(0, unreadable);
        Assert.Equal(
            new (string, MelonPhase, string?)[]
            {
                ("UserLibs", MelonPhase.UserLibs, null),
                ("Plugins", MelonPhase.Plugins, null),
                ("Mods", MelonPhase.Mods, null),
                (@"UserLibs\B", MelonPhase.UserLibs, @"UserLibs\B"),
                (@"UserLibs\B\Plugins", MelonPhase.UserLibs, @"UserLibs\B"), // Plugins below a library folder stays a library folder
                (@"Plugins\C", MelonPhase.Plugins, @"Plugins\C"),
                (@"Plugins\C\Mods", MelonPhase.Plugins, @"Plugins\C"), // Mods below a plugin folder stays a plugin folder
                (@"Plugins\C\Plugins", MelonPhase.Plugins, @"Plugins\C"),
                (@"Mods\A", MelonPhase.Mods, @"Mods\A"),
                (@"Mods\A\Extra", MelonPhase.Mods, @"Mods\A"),
                (@"Mods\A\Extra\Mods", MelonPhase.Mods, @"Mods\A"),
                (@"Mods\A\Plugins", MelonPhase.Plugins, @"Mods\A"),
                (@"Mods\A\UserLibs", MelonPhase.UserLibs, @"Mods\A"),
            },
            folders);

        Assert.Contains(InstallLayout.Folders(tree, tree.Root, false, manifestNotNeeded: true, out _), f => f.Path.EndsWith("NoManifest", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_that_cant_be_read_is_counted_and_an_unreadable_dll_of_ours_still_counts()
    {
        FakeTree tree = NewLayout(new FakeTree()).Broken(@"Plugins\T2TOnline.dll").Add(@"Mods\Locked\manifest.json").Unreadable(@"Mods\Locked");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(new[] { @"Plugins\T2TOnline.dll" }, verdict.Entries.Select(e => e.Path));
        Assert.Equal(1, verdict.Unreadable);
        Assert.EndsWith("1 folder(s) couldn't be read and weren't checked.", InstallLayout.LogLine(verdict, "0.11.0-beta"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_player_named_folder_is_a_label_in_the_log_and_itself_on_screen()
    {
        FakeTree tree = NewLayout(NewLayout(new FakeTree()), @"Mods\Alex's copy", Old, V10).Dll(@"Mods\Alex's copy\Plugins\Notes.dll", "Notes", V10, "1.0");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));
        string line = InstallLayout.LogLine(verdict, "0.11.0-beta");

        Assert.DoesNotContain("Alex", line, StringComparison.Ordinal);
        Assert.Contains(@"Mods\[folder 1]\Plugins\T2TOnline.dll", line, StringComparison.Ordinal);
        Assert.Contains("Mods/Alex's copy/Plugins/T2TOnline.dll", InstallLayout.ForScreen(verdict.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompts_list_uses_slashes_and_only_what_its_fonts_draw_and_stays_short()
    {
        var entries = new List<InstallEntry>
        {
            new(@"Plugins\T2TOnline.dll", string.Empty),
            new(@"Mods\Copy $1 ~ |x|\Plugins\T2TOnline.dll", string.Empty),
            new(@"Mods\Копия\UserLibs\T2TOnline.Core.dll", string.Empty),
            new(@"Mods\" + new string('a', 100) + @"\Plugins\T2TOnline.dll", string.Empty),
        };
        entries.AddRange(Enumerable.Range(1, 7).Select(i => new InstallEntry($@"UserLibs\T2TOnline.Core ({i}).dll", string.Empty)));

        string shown = InstallLayout.ForScreen(entries);

        Assert.StartsWith("Plugins/T2TOnline.dll, Mods/Copy ?1 ? ?x?/Plugins/T2TOnline.dll, Mods/?????/UserLibs/T2TOnline.Core.dll, ...", shown, StringComparison.Ordinal);
        Assert.EndsWith("UserLibs/T2TOnline.Core (4).dll (+3)", shown, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', shown);
        Assert.All(shown, c => Assert.True(c is >= ' ' and <= '~' && !"$@^`|~".Contains(c, StringComparison.Ordinal), $"'{c}' isn't drawn by the prompt's fonts"));
        Assert.All(shown.Split(", "), path => Assert.True(path.Length <= InstallLayout.ScreenPathLength + " (+3)".Length, path));
        Assert.Equal(@"Plugins\T2TOnline.dll, LICENSE", InstallLayout.ForNotice(new[] { new InstallEntry(@"Plugins\T2TOnline.dll", string.Empty), new InstallEntry("LICENSE", string.Empty) }));
    }

    [Fact]
    public void Pdbs_beside_what_runs_are_kept_and_stray_ones_are_not()
    {
        FakeTree tree = NewLayout(new FakeTree()).Add(@"UserLibs\T2TOnline.Steam.pdb");

        InstallVerdict verdict = InstallLayout.Decide(tree, Facts(tree));

        Assert.Equal(new[] { @"UserLibs\T2TOnline.Steam.pdb" }, verdict.Entries.Select(e => e.Path));
    }

    [Fact]
    public void A_line_for_melonloaders_log_names_the_game_folder_by_its_label()
    {
        const string game = @"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape";
        string report = $@"MelonLoader remakes its interop assemblies on this launch (the game changed): 3 old files cleared, 1 couldn't be, so its generator may stop the game (X.dll: The process cannot access the file '{game}\MelonLoader\Il2CppAssemblies\X.dll' because it is being used by another process.).";

        string line = InstallLayout.LabelGameFolder(report, game + @"\", game.ToUpperInvariant(), null, "C:");

        Assert.DoesNotContain("Program Files", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"'<game folder>\MelonLoader\Il2CppAssemblies\X.dll'", line, StringComparison.Ordinal);
        Assert.Equal("<game folder>/UserData", InstallLayout.LabelGameFolder(game.Replace('\\', '/') + "/UserData", game));
        Assert.Equal(@"C:\x and D:\y", InstallLayout.LabelGameFolder(@"C:\x and D:\y", "C:", @"D:\"));
    }

    // The zip's tree: a Melon folder with its manifest, documents, plugin and two libraries (each with its debug symbols).
    private static FakeTree NewLayout(FakeTree tree, string folder = Home, string build = New, Version? version = null)
    {
        Version v = version ?? V11;
        return tree.Add($@"{folder}\manifest.json").Add($@"{folder}\LICENSE.txt").Add($@"{folder}\THIRD-PARTY-NOTICES.txt")
            .Dll($@"{folder}\Plugins\T2TOnline.dll", InstallLayout.PluginName, v, build).Add($@"{folder}\Plugins\T2TOnline.pdb")
            .Dll($@"{folder}\UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, v, build).Add($@"{folder}\UserLibs\T2TOnline.Core.pdb")
            .Dll($@"{folder}\UserLibs\T2TOnline.Steam.dll", InstallLayout.SteamName, v, build).Add($@"{folder}\UserLibs\T2TOnline.Steam.pdb");
    }

    // 0.10.0 Beta's six flat files.
    private static FakeTree OldFlat(FakeTree tree) =>
        tree.Dll(@"Plugins\T2TOnline.dll", InstallLayout.PluginName, V10, Old).Add(@"Plugins\T2TOnline.pdb")
            .Dll(@"UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V10, Old).Add(@"UserLibs\T2TOnline.Core.pdb")
            .Dll(@"UserLibs\T2TOnline.Steam.dll", InstallLayout.SteamName, V10, Old).Add(@"UserLibs\T2TOnline.Steam.pdb");

    // The new build installed by hand the old way.
    private static FakeTree NewFlat(FakeTree tree) =>
        tree.Dll(@"Plugins\T2TOnline.dll", InstallLayout.PluginName, V11, New).Add(@"Plugins\T2TOnline.pdb")
            .Dll(@"UserLibs\T2TOnline.Core.dll", InstallLayout.CoreName, V11, New).Add(@"UserLibs\T2TOnline.Core.pdb")
            .Dll(@"UserLibs\T2TOnline.Steam.dll", InstallLayout.SteamName, V11, New).Add(@"UserLibs\T2TOnline.Steam.pdb");

    // What the process loaded: by default the zip's three files, all of the new build.
    private static InstallFacts Facts(
        FakeTree tree,
        string plugin = @"Mods\T2TOnline\Plugins\T2TOnline.dll",
        string core = @"Mods\T2TOnline\UserLibs\T2TOnline.Core.dll",
        string steam = @"Mods\T2TOnline\UserLibs\T2TOnline.Steam.dll",
        string coreBuild = New,
        Version? coreVersion = null) =>
        new(
            tree.Root,
            SubfoldersOff: false,
            ManifestNotNeeded: false,
            new LoadedPart(InstallLayout.PluginName, tree.P(plugin), V11, New),
            new LoadedPart(InstallLayout.CoreName, tree.P(core), coreVersion ?? V11, coreBuild),
            new LoadedPart(InstallLayout.SteamName, tree.P(steam), V11, New),
            V11,
            V11);

    /// <summary>A game folder in memory: files with their bytes and, for DLLs, the identity a real file's metadata would give.</summary>
    private sealed class FakeTree : IInstallTree
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyFacts?> _assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

        public string Root { get; } = Path.GetFullPath(Path.Combine("install-check", "Tape to Tape"));

        public string P(string relative) => Path.Combine(Root, relative.Replace('\\', Path.DirectorySeparatorChar));

        public FakeTree Add(string relative, byte[]? content = null)
        {
            string path = P(relative);
            _files[path] = content ?? Array.Empty<byte>();
            for (string? folder = Path.GetDirectoryName(path); folder != null && folder.Length > Root.Length; folder = Path.GetDirectoryName(folder))
            {
                _directories.Add(folder);
            }

            return this;
        }

        public FakeTree Dll(string relative, string name, Version version, string? build)
        {
            Add(relative);
            _assemblies[P(relative)] = new AssemblyFacts(name, version, build);
            return this;
        }

        public FakeTree Broken(string relative)
        {
            Add(relative);
            _assemblies[P(relative)] = null;
            return this;
        }

        public FakeTree Unreadable(string relative)
        {
            _unreadable.Add(P(relative));
            return this;
        }

        public IReadOnlyList<string>? Directories(string path) =>
            _unreadable.Contains(path) ? null : _directories.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        public IReadOnlyList<string>? Files(string path) =>
            _unreadable.Contains(path) ? null : _files.Keys.Where(f => string.Equals(Path.GetDirectoryName(f), path, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        public bool FileExists(string path) => _files.ContainsKey(path);

        public AssemblyFacts? Assembly(string path) => _assemblies.TryGetValue(path, out AssemblyFacts? facts) ? facts : null;

        public byte[]? Head(string path, int max) => _files.TryGetValue(path, out byte[]? bytes) ? bytes.Take(max).ToArray() : null;
    }
}
