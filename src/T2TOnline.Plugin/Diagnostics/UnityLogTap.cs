#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MelonLoader.Utils;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// Copies the game's own Unity log output into the session log, so the game's messages sit in order with the mod's in
/// one file (the game also writes its own Player.log, under %USERPROFILE%\AppData\LocalLow\Excellent
/// Rectangle\Tape to Tape). Kept: warnings, errors and exceptions, and the game's controller-assignment lines
/// (<c>[PlayerAssigner]</c>, <c>[ControllerConnection]</c>: which controller the game gave which player, which the mod's
/// input lines point at); capped so a noisy game can't flood the log.
/// </summary>
internal static class UnityLogTap
{
    private const int MaxLines = 2000;
    private const int MaxStackLines = 12;
    private static readonly string[] KeptPrefixes = { "[PlayerAssigner]", "[ControllerConnection]" };
    private static Application.LogCallback? _callback;
    private static int _lines;
    private static string? _last;
    private static int _repeats;
    private static KeyValuePair<string, string>[]? _folders;

    /// <summary>Subscribes to Unity's log. Safe to call once.</summary>
    public static void Install()
    {
        if (_callback != null)
        {
            return;
        }

        _callback = DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnLog));
        Application.add_logMessageReceived(_callback);
        ModLog.AddFolders(_folders ??= Folders()); // the game's save folder becomes a label in every line, not only the game's
        ModLog.Info("Unity log tap: on (game warnings, errors and controller-assignment lines are copied here, prefixed [game]).");
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        try
        {
            bool keep = type != LogType.Log || Array.Exists(KeptPrefixes, prefix => condition?.StartsWith(prefix, StringComparison.Ordinal) == true);
            if (!keep || _lines >= MaxLines)
            {
                return;
            }

            // A message identical to the previous one is counted, not repeated.
            string key = $"{type}|{condition}";
            if (key == _last)
            {
                _repeats++;
                return;
            }

            if (_repeats > 0)
            {
                ModLog.Info($"[game] (previous message repeated {_repeats} more times)");
                _repeats = 0;
            }

            _last = key;
            if (++_lines == MaxLines)
            {
                ModLog.Warn($"Unity log tap: {MaxLines} lines copied; the rest of this session's game log is dropped.");
                return;
            }

            // Without the player's folders: a game error naming a co-op slot's file would carry the Windows user name.
            string text = type is LogType.Exception or LogType.Error && !string.IsNullOrEmpty(stackTrace)
                ? $"[game {type}] {Redact(condition)}{Environment.NewLine}{Redact(FirstLines(stackTrace, MaxStackLines))}"
                : $"[game {type}] {Redact(condition)}";
            if (type == LogType.Log)
            {
                ModLog.Info(text);
            }
            else
            {
                ModLog.Warn(text);
            }
        }
        catch (Exception ex)
        {
            // Never throw into Unity's logger.
            ModLog.Error("Unity log tap failed", ex);
        }
    }

    /// <summary>
    /// <paramref name="text"/> with the folders that would name the player (their profile, the game's save folder, the
    /// game folder, the mod's data folder) replaced by placeholders, as <see cref="Campaign.CampaignFiles.ErrorText"/> does
    /// for the mod's own messages, and the names the game's own lines carry (a skater's, a custom team's: player-written)
    /// reduced to their shape (<see cref="GameLogText"/>). The folders are looked up once; if they can't be, the text is
    /// dropped rather than risk a full path.
    /// </summary>
    private static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        _folders ??= Folders();
        return _folders.Length == 0
            ? "(the message isn't copied: the folders that would name you couldn't be looked up)"
            : GameLogText.Reduce(SavePathText.Redact(text, _folders), LogNames.TextShape, out _);
    }

    private static KeyValuePair<string, string>[] Folders()
    {
        try
        {
            return new[]
            {
                new KeyValuePair<string, string>(Application.persistentDataPath, "<save folder>"),
                new KeyValuePair<string, string>(MelonEnvironment.GameRootDirectory, "<game folder>"),
                new KeyValuePair<string, string>(Campaign.CampaignFiles.DataFolder, Campaign.CampaignFiles.DataFolderLabel),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            };
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Unity log tap: the folders to keep out of the log couldn't be looked up ({ex.GetType().Name}); game messages are left out.");
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    private static string FirstLines(string text, int count)
    {
        string[] lines = text.TrimEnd().Split('\n');
        return lines.Length <= count
            ? text.TrimEnd()
            : string.Join("\n", lines, 0, count) + $"\n  ... {lines.Length - count} more stack lines";
    }
}
#endif
