using System;
using System.IO;
using System.Text.RegularExpressions;

namespace T2TOnline.Core.Tests.Startup;

/// <summary>
/// On a mixed install the plugin keeps crash reporting and analytics off without T2TOnline.Core, which may be another build
/// (InertHousekeeping). The code on that path must name nothing of Core and write nothing to the session log (which uses
/// Core): its lines go through EarlyLog, which reaches the session log only from methods of its own.
/// </summary>
public class InertPathSourceTests
{
    private static readonly string[] InertPath =
    {
        @"Startup\InertHousekeeping.cs",
        @"Startup\EarlyHousekeeping.cs",
        @"Startup\EarlyAnalytics.cs",
        @"Startup\CrashReporting.cs",
        @"Startup\CrashReporterSwitches.cs",
        @"Startup\RipRelativeLoad.cs",
        @"Startup\EngineTelemetry.cs",
        @"Startup\Housekeeping.cs",
        @"Native\Il2CppApi.cs",
        @"Native\MemoryAccess.cs",
        @"Hooks\Il2CppTypeLocator.cs",
    };

    // Core's namespace, and the plugin's code that uses Core: the session log and its helpers.
    private static readonly Regex UsesCore = new(@"T2TOnline\.Core|\bModLog\b|\bSafeText\b|\bLogScrub\b|\bModSettings\b|\bModVersion\b|\bDevOutput\b", RegexOptions.CultureInvariant);

    [Fact]
    public void The_inert_path_names_nothing_of_core()
    {
        foreach (string file in InertPath)
        {
            string[] lines = File.ReadAllLines(PluginFile(file));
            for (int i = 0; i < lines.Length; i++)
            {
                string code = CodeOf(lines[i]);
                Assert.False(UsesCore.IsMatch(code), $"{file}:{i + 1} names Core or the session log: {lines[i].Trim()}");
            }
        }
    }

    [Fact]
    public void Early_log_reaches_the_session_log_only_from_its_own_methods()
    {
        string[] lines = File.ReadAllLines(PluginFile(@"Startup\EarlyLog.cs"));
        int uses = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string code = CodeOf(lines[i]);
            if (!code.Contains("ModLog.", StringComparison.Ordinal))
            {
                continue;
            }

            uses++;
            Assert.Matches(@"private static (void Session(Info|Warn|Error)|string SessionErrorText)\(", code);
            Assert.Contains("[MethodImpl(MethodImplOptions.NoInlining)]", lines[i - 1], StringComparison.Ordinal);
        }

        Assert.Equal(4, uses);
    }

    // MelonLoader ties a subscription to the assembly that declares its method (static methods too) and drops them all when
    // a plugin of that assembly unregisters: on a mixed install every event is taken after the plugin unregisters, or the
    // housekeeping's first frame, its checks and its switch-off at quit never run.
    [Fact]
    public void The_inert_path_takes_events_only_after_the_plugin_unregisters()
    {
        string[] stop = File.ReadAllLines(PluginFile(@"Startup\InertPlugin.cs"));
        int unregister = Array.FindIndex(stop, line => CodeOf(line).Contains("plugin.Unregister(", StringComparison.Ordinal));
        Assert.True(unregister >= 0, "InertPlugin no longer unregisters the plugin: check where its events are taken");
        for (int i = 0; i < stop.Length; i++)
        {
            string code = CodeOf(stop[i]);
            if (code.Contains(".Subscribe", StringComparison.Ordinal))
            {
                Assert.True(i > unregister, $"InertPlugin.cs:{i + 1} subscribes before the plugin unregisters: {stop[i].Trim()}");
            }
        }

        Assert.Contains(stop[(unregister + 1)..], line => CodeOf(line).Contains("InertHousekeeping.Subscribe", StringComparison.Ordinal));

        // InertHousekeeping subscribes only in its Subscribe method, not in Start (called before the plugin unregisters).
        string[] housekeeping = File.ReadAllLines(PluginFile(@"Startup\InertHousekeeping.cs"));
        int subscribe = Array.FindIndex(housekeeping, line => CodeOf(line).Contains("public static void Subscribe()", StringComparison.Ordinal));
        Assert.True(subscribe >= 0);
        for (int i = 0; i < housekeeping.Length; i++)
        {
            if (CodeOf(housekeeping[i]).Contains("MelonEvents.", StringComparison.Ordinal))
            {
                int method = Array.FindLastIndex(housekeeping, i, line => line.StartsWith("    public static", StringComparison.Ordinal) || line.StartsWith("    private static", StringComparison.Ordinal));
                Assert.True(method == subscribe, $"InertHousekeeping.cs:{i + 1} takes a MelonLoader event outside Subscribe: {housekeeping[i].Trim()}");
            }
        }
    }

    // A line without its comment (a '//' after code, or a whole comment line).
    private static string CodeOf(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static string PluginFile(string relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "T2TOnline.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "T2TOnline.Plugin", relative.Replace('\\', Path.DirectorySeparatorChar));
    }
}
