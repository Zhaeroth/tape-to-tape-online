using System;
using System.IO;
using MelonLoader.Utils;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// Whether Steam refused relay-only connections on this PC (D133): then Settings > Online shows Direct Connections, the way to play anyway. Known
/// this start from the session's own start-up check (<see cref="Seen"/>), and across starts from a marker in the mod's data folder that holds the
/// time in UTC and nothing else; the marker goes when relay only holds again (<see cref="Held"/>). Never throws.
/// </summary>
internal static class RelayRefusal
{
    private const string Label = "UserData/T2TOnline/" + DirectConnectionsRule.MarkerFileName;

    private static bool? _refused;

    /// <summary>True when Steam refused relay only at this start or at an earlier one (and hasn't kept it since).</summary>
    public static bool Refused => _refused ??= Read();

    /// <summary>The session's start-up check: Steam wouldn't keep relay only. Remembered for this start and the next ones.</summary>
    public static void Seen()
    {
        _refused = true;
        try
        {
            string path = MarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DirectConnectionsRule.MarkerText(DateTime.UtcNow));
            ModLog.Info($"Settings: Steam refused relay only: Settings > Online shows Direct Connections from now on ({Label}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Settings: Steam refused relay only, but {Label} couldn't be written ({ex.GetType().Name}); Settings > Online shows Direct Connections this start only.");
        }
    }

    /// <summary>The session's start-up check: relay only holds. A marker from an earlier start goes.</summary>
    public static void Held()
    {
        _refused = false;
        try
        {
            string path = MarkerPath();
            if (File.Exists(path))
            {
                File.Delete(path);
                ModLog.Info($"Settings: relay only holds again: {Label} removed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Settings: relay only holds again, but {Label} couldn't be removed ({ex.GetType().Name}).");
        }
    }

    private static bool Read()
    {
        try
        {
            string path = MarkerPath();
            return File.Exists(path) && DirectConnectionsRule.IsMarker(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string MarkerPath() => Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", DirectConnectionsRule.MarkerFileName);
}
