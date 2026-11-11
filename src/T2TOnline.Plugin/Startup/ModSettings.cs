using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Core.SettingsFiles;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;

namespace T2TOnline.Plugin.Startup;

/// <summary>What a friend plays with online (the <see cref="ModSettings.OnlineInput"/> setting).</summary>
internal enum OnlineInputChoice
{
    /// <summary>A gamepad if one is connected, else the keyboard and mouse.</summary>
    Auto,

    /// <summary>A gamepad (joining needs one).</summary>
    Gamepad,

    /// <summary>The keyboard and mouse buttons, even with a gamepad connected.</summary>
    KeyboardMouse,
}

/// <summary>
/// The mod's settings, in its own file <c>UserData\T2TOnline\settings.cfg</c>, section <c>[T2TOnline]</c> (D126; moved there
/// once from MelonLoader's shared <c>UserData\MelonPreferences.cfg</c> by <see cref="SettingsFile"/>). The player's settings
/// are always written; the developer's only when not at their default (still read when a developer types them in).
/// </summary>
internal static class ModSettings
{
    private const string Category = "T2TOnline";

    private static MelonPreferences_Category? _category;

    // Each setting's entry by name, made once the settings are open: the getters run every frame in places, and MelonLoader's
    // own lookup (GetEntry) searches the list with a new closure on every call. MelonLoader loads a file's values into the
    // entries it has, so these stay the category's entries for the whole game.
    private static Dictionary<string, MelonPreferences_Entry>? _entries;

    // A check tour's changes, held in memory over the entries (read first by every getter; none outside a tour's changes).
    private static readonly HeldValues Held = new();

    // DevTools as the file gave it when Load finished: MelonLoader reloads the file when it changes on disk, and the keys,
    // the panel and the tours are made (or not) at start-up, so every reader keeps the value the launch began with.
    private static bool? _devToolsAtLaunch;

    /// <summary>M0 test: one-way delay added to the F8 controller loopback, to feel online latency on one PC.</summary>
    public static int LoopbackDelayMs => Number(nameof(LoopbackDelayMs), 0);

    /// <summary>Developer: measure the game's replay snapshots during matches (sizes, rates; Ctrl+F9 captures).</summary>
    public static bool SnapshotMeasure => Flag(nameof(SnapshotMeasure), false);

    /// <summary>
    /// Developer: every developer key (F1's panel, F7, Shift+F7, Ctrl+F5, Ctrl+F6, F9, and F2, F3, F4, F8, F10, Ctrl+F10, F11, Ctrl+F11, Ctrl+F12,
    /// Ctrl+F9) and the check tours. Off is the players' build (D142): no key but Insert, every function on the game's own screens.
    /// Read once per launch, when the settings are loaded: a change in the file while the game runs takes effect at the next start
    /// (the keys and the texts that name them stay the launch's). False before <see cref="Load"/>.
    /// </summary>
    public static bool DevTools => _devToolsAtLaunch ?? Flag(nameof(DevTools), false);

    /// <summary>
    /// Developer: the status box in the top-left corner (who is connected, how, and every notice) and the net overlay (F9). Off, a player sees nothing about networking
    /// unless it is wrong, and then in the game's own toast or prompt; the box only carries what those can't (the native screens off or failing, or the mod stopped after an error).
    /// </summary>
    public static bool ShowStatusBox => Flag(nameof(ShowStatusBox), false);

    /// <summary>
    /// Developer: an online host also records each match it streams to UserData/T2TOnline/dev/streams (F2 and the analysis
    /// tools read them). Off by default: it's a second encode per snapshot and about 10 MB per match on disk (the newest 20
    /// kept, at most 250 MB). F3 recordings are always written.
    /// </summary>
    public static bool RecordOnlineMatches => Flag(nameof(RecordOnlineMatches), false);

    /// <summary>Developer: on a game build the mod wasn't tested on, run the offline test tools anyway (never online).</summary>
    public static bool AllowUntestedBuild => Flag(nameof(AllowUntestedBuild), false);

    /// <summary>M2 test: one-way delay of the simulated link F4 playback runs snapshots through.</summary>
    public static int PlaybackLinkDelayMs => Number(nameof(PlaybackLinkDelayMs), 0);

    /// <summary>M2 test: extra random delay per snapshot (0 to this) on the simulated link.</summary>
    public static int PlaybackLinkJitterMs => Number(nameof(PlaybackLinkJitterMs), 0);

    /// <summary>M2 test: share of snapshots the simulated link loses.</summary>
    public static int PlaybackLinkLossPercent => Number(nameof(PlaybackLinkLossPercent), 0);

    /// <summary>Test: milliseconds Steam adds to everything this PC sends online (0 = off). Both ways on the one-PC test.</summary>
    public static int SimulatedLagMs => Number(nameof(SimulatedLagMs), 0);

    /// <summary>Test: mean extra milliseconds of random delay on what this PC sends (0 = off).</summary>
    public static int SimulatedJitterMs => Number(nameof(SimulatedJitterMs), 0);

    /// <summary>Test: percent of the packets this PC sends that Steam drops (0 = off).</summary>
    public static int SimulatedLossPercent => Number(nameof(SimulatedLossPercent), 0);

    /// <summary>Online: achievements earned together in a session unlock for everyone (host sends, friends unlock).</summary>
    public static bool ShareAchievements => Flag(nameof(ShareAchievements), true);

    /// <summary>Online host: an AI punch at a friend waits for their block, up to their lag (the fight grace window, D35).</summary>
    public static bool FightGraceWindow => Flag(nameof(FightGraceWindow), true);

    /// <summary>Online host: the AI plays a friend's skater while they're away (dropped, silent or unplugged for 3 s).</summary>
    public static bool AiStandIn => Flag(nameof(AiStandIn), true);

    /// <summary>
    /// Online campaign: everyone's own profile progresses from a run played together (stats, unlocks, quests, squad wins,
    /// run history), each at their own thresholds (D42). Off: you send none, and what comes in waits until you turn it back on
    /// in the same online game.
    /// </summary>
    public static bool ShareProgress => Flag(nameof(ShareProgress), true);

    /// <summary>
    /// Online host: friends open the game's own Nurse procedure screen and follow your presses on it. Off: they
    /// see that screen as text over their event screen. Read at each Nurse event.
    /// </summary>
    public static bool NurseRealScreen => Flag(nameof(NurseRealScreen), true);

    /// <summary>
    /// The native lobby: the Online row on the title, its group and the lobby's screens. A developer setting: off (with <see cref="DevTools"/>), F7,
    /// Shift+F7, F1 and Steam joins run a session as ever; without the developer tools it reads as on whatever the file says, since a player has no
    /// other way to host (<see cref="PlayersBuild.NativeLobbyOn"/>, logged once by <see cref="LogBuildMode"/>).
    /// </summary>
    public static bool NativeLobby => PlayersBuild.NativeLobbyOn(DevTools, Flag(nameof(NativeLobby), true));

    /// <summary>
    /// Online: let Steam connect this PC straight to the other players when it can (D133). Off (the default), every
    /// connection goes through Steam's relays and no player learns another's IP address; on, the players you connect to
    /// can see yours. Read at start-up. Read from its entry alone: a check tour can't change it, even in memory
    /// (<see cref="TourSettingHold"/>).
    /// </summary>
    public static bool AllowDirectConnections => Entry<bool>(nameof(AllowDirectConnections))?.Value ?? false;

    /// <summary>
    /// Online friend: what you play with when you join (Auto: a gamepad if one is connected, else the keyboard and mouse).
    /// Anything else in the file reads as Auto.
    /// </summary>
    public static OnlineInputChoice OnlineInput =>
        Held.TryGet(nameof(OnlineInput), out OnlineInputChoice held) ? held
        : TryReadChoice(Entry<string>(nameof(OnlineInput))?.Value, out OnlineInputChoice choice) ? choice : OnlineInputChoice.Auto;

    /// <summary>
    /// Settings > Online's Play Online With (and the developer's panel): sets <see cref="OnlineInput"/> and saves the settings file. False, with why, when it couldn't
    /// be set or saved (then it's set for this session only). While a check tour runs it's set in memory only (<see cref="HoldSetting"/>).
    /// </summary>
    public static bool TrySetOnlineInput(OnlineInputChoice choice, out string problem)
    {
        MelonPreferences_Entry<string>? entry = _category?.GetEntry<string>(nameof(OnlineInput));
        if (entry == null)
        {
            problem = "the settings weren't loaded at start-up";
            return false;
        }

        if (HoldSetting(nameof(OnlineInput), choice, out bool held, out problem))
        {
            return held;
        }

        entry.Value = choice.ToString();
        if (!SettingsFile.Save(_category, out string why))
        {
            problem = $"it's {choice} for this session, but {why}";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Online host: the co-op save slot (1-3) campaign runs are kept in while hosting, apart from the solo run (D46).</summary>
    public static int CoopSlot => Math.Clamp(Number(nameof(CoopSlot), 1), 1, 3);

    /// <summary>
    /// Sets <see cref="CoopSlot"/> (1-3) and saves the settings. False when the settings aren't loaded. A file that can't be
    /// saved still leaves the slot set for this session (true, and a warning in the log), so the caller tells the run sync
    /// about the slot that's in use rather than leaving it on the old one. While a check tour runs it's set in memory only.
    /// </summary>
    public static bool TrySetCoopSlot(int slot)
    {
        MelonPreferences_Entry<int>? entry = _category?.GetEntry<int>(nameof(CoopSlot));
        if (entry == null)
        {
            return false;
        }

        if (HoldSetting(nameof(CoopSlot), Math.Clamp(slot, 1, 3), out bool held, out _))
        {
            return held;
        }

        entry.Value = Math.Clamp(slot, 1, 3);
        if (!SettingsFile.Save(_category, out string why))
        {
            ModLog.Warn($"Settings: co-op slot {entry.Value} is in use for this session, but {why}, so the next start uses the slot saved before.");
        }

        return true;
    }

    /// <summary>
    /// Opens the settings (D126): from <c>UserData\T2TOnline\settings.cfg</c>, moved there once from MelonLoader's shared
    /// file, or created with the defaults. Every line it logs names files by label, never by path; which settings aren't at
    /// their defaults is the log header's one line (<c>ModLog.WriteSettings</c>), not repeated here.
    /// </summary>
    public static void Load()
    {
        string path = Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", SettingsFile.FileName);
        string shared = Path.Combine(MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
        MelonPreferences_Category category = SettingsFile.Open(Category, "T2T Online", path, shared, CreateEntries, ModLog.Info, ModLog.Warn);
        var entries = new Dictionary<string, MelonPreferences_Entry>(StringComparer.Ordinal);
        foreach (MelonPreferences_Entry entry in category.Entries)
        {
            entries[entry.Identifier] = entry;
        }

        _entries = entries;
        _category = category;
        _devToolsAtLaunch = Flag(nameof(DevTools), false);
        ModLog.Info($"Settings: read from {(SettingsFile.OnOwnFile ? SettingsFile.Label : SettingsFile.SharedLabel)}.");
    }

    /// <summary>
    /// Once, after the log header's settings line: which build runs (<see cref="PlayersBuild.ModeLine"/>), and, in the players' build, that a
    /// native lobby the file turns off reads as on (<see cref="PlayersBuild.NativeLobbyLine"/>). Never throws.
    /// </summary>
    public static void LogBuildMode()
    {
        try
        {
            bool devTools = DevTools;
            ModLog.Info(PlayersBuild.ModeLine(devTools));
            if (PlayersBuild.NativeLobbyOverridden(devTools, Flag(nameof(NativeLobby), true)))
            {
                ModLog.Info(PlayersBuild.NativeLobbyLine);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings: the build mode couldn't be read ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The settings not at their defaults, as one short line part for the log (<c>CoopSlot = 3, DevTools = true</c>, or
    /// <c>none</c>): names and values only, never text from the file (an <see cref="OnlineInput"/> that isn't a choice is
    /// said so, not quoted). "not loaded" before <see cref="Load"/> (it runs in <c>OnInitializeMelon</c>).
    /// </summary>
    public static string NonDefaultSummary()
    {
        if (_category == null)
        {
            return "not loaded";
        }

        var notDefault = new List<KeyValuePair<string, string>>();
        foreach (MelonPreferences_Entry entry in _category.Entries)
        {
            switch (entry)
            {
                case MelonPreferences_Entry<bool> flag when flag.Value != flag.DefaultValue:
                    notDefault.Add(new(entry.Identifier, flag.Value ? "true" : "false"));
                    break;
                case MelonPreferences_Entry<int> number when number.Value != number.DefaultValue:
                    notDefault.Add(new(entry.Identifier, number.Value.ToString(CultureInfo.InvariantCulture)));
                    break;
                case MelonPreferences_Entry<string> text when entry.Identifier == nameof(OnlineInput):
                    if (!TryReadChoice(text.Value, out OnlineInputChoice choice))
                    {
                        notDefault.Add(new(entry.Identifier, "Auto (the file's value isn't a choice)"));
                    }
                    else if (choice != OnlineInputChoice.Auto)
                    {
                        notDefault.Add(new(entry.Identifier, choice.ToString()));
                    }

                    break;
            }
        }

        return SettingsText.Summary(notDefault);
    }

    /// <summary>
    /// Settings > Online and the developer's panel (F1): turns <see cref="ShareAchievements"/>, <see cref="ShareProgress"/>, <see cref="FightGraceWindow"/>,
    /// <see cref="AiStandIn"/>, <see cref="NativeLobby"/> or <see cref="AllowDirectConnections"/> on or off and saves the settings file at once. Those
    /// properties read their entry each time, so the change takes effect at once (Direct Connections at the next start: it's read then). False, with why, when the setting couldn't be changed or saved. While a check
    /// tour runs the change is held in memory and nothing is written, and Direct Connections is refused (<see cref="HoldSetting"/>).
    /// </summary>
    public static bool TrySetSwitch(string name, bool value, out string problem)
    {
        // First, whatever the list below allows: a tour never changes Direct Connections, even in memory.
        if (TourSettingHold.Decide(TourHolds.On, name) == TourSettingAction.Refuse)
        {
            TourHolds.SettingRefused(name);
            problem = "a check tour runs, and a tour never changes it";
            return false;
        }

        if (name is not (nameof(ShareAchievements) or nameof(ShareProgress) or nameof(FightGraceWindow) or nameof(AiStandIn) or nameof(NativeLobby) or nameof(AllowDirectConnections)))
        {
            problem = $"{name} isn't a setting the mod's screens change";
            return false;
        }

        MelonPreferences_Entry<bool>? entry = _category?.GetEntry<bool>(name);
        if (entry == null)
        {
            problem = "the settings weren't loaded at start-up";
            return false;
        }

        if (HoldSetting(name, value, out bool held, out problem))
        {
            return held;
        }

        entry.Value = value;
        if (!SettingsFile.Save(_category, out string why))
        {
            problem = $"it's {(value ? "on" : "off")} for this session, but {why}";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// After a check tour, when the game stays open: every value it held in memory goes, so the saved ones apply again.
    /// Returns the settings' names (sorted; empty when none was held).
    /// </summary>
    public static IReadOnlyList<string> DropHeld() => Held.Drop();

    /// <summary>
    /// A check tour's own developer switch (only <see cref="RecordOnlineMatches"/>: the campaign tour's <c>record=on</c>), held
    /// in memory for the tour as <see cref="TrySetSwitch"/> holds a player's: never written, dropped with the tour's holds. False,
    /// with why, when no tour holds or the switch isn't one a tour sets.
    /// </summary>
    public static bool TryHoldForTour(string name, bool value, out string problem)
    {
        if (name != nameof(RecordOnlineMatches))
        {
            problem = $"{name} isn't a switch a check tour sets";
            return false;
        }

        if (!HoldSetting(name, value, out bool held, out problem))
        {
            problem = "no check tour holds the settings";
            return false;
        }

        return held;
    }

    /// <summary>
    /// A change of setting <paramref name="name"/> while a check tour runs (<see cref="TourSettingHold"/>): held in memory, so
    /// the getters read it and nothing reaches the file (MelonLoader saves every entry's value at quit, so the entry isn't
    /// changed either); or, for Direct Connections, refused. True when it was decided here (<paramref name="held"/> says
    /// whether it took, <paramref name="problem"/> why not); false when no tour runs and the caller saves as ever.
    /// </summary>
    private static bool HoldSetting(string name, object value, out bool held, out string problem)
    {
        held = false;
        problem = string.Empty;
        switch (TourSettingHold.Decide(TourHolds.On, name))
        {
            case TourSettingAction.Write:
                return false;
            case TourSettingAction.Refuse:
                TourHolds.SettingRefused(name);
                problem = "a check tour runs, and a tour never changes it";
                return true;
            default:
                Held.Hold(name, value);
                TourHolds.SettingHeld();
                held = true;
                return true;
        }
    }

    /// <summary>A switch's value: a check tour's held value first (<see cref="Held"/>), else its entry's, else <paramref name="fallback"/>.</summary>
    private static bool Flag(string name, bool fallback) =>
        Held.TryGet(name, out bool held) ? held : Entry<bool>(name)?.Value ?? fallback;

    /// <summary>A number's value: a check tour's held value first (<see cref="Held"/>), else its entry's, else <paramref name="fallback"/>.</summary>
    private static int Number(string name, int fallback) =>
        Held.TryGet(name, out int held) ? held : Entry<int>(name)?.Value ?? fallback;

    /// <summary>The entry of setting <paramref name="name"/>, or null before <see cref="Load"/> (or when it isn't one of type <typeparamref name="T"/>): one dictionary read, nothing allocated.</summary>
    private static MelonPreferences_Entry<T>? Entry<T>(string name) =>
        _entries != null && _entries.TryGetValue(name, out MelonPreferences_Entry? entry) ? entry as MelonPreferences_Entry<T> : null;

    /// <summary>The entries, in the file's order: the player's first, then the developer's (written only when not at their default).</summary>
    private static void CreateEntries(MelonPreferences_Category category)
    {
        category.CreateEntry(nameof(ShareAchievements), true, "Share achievements", SettingDescriptions.ShareAchievements);
        category.CreateEntry(nameof(ShareProgress), true, "Share progress", SettingDescriptions.ShareProgress);
        category.CreateEntry(nameof(FightGraceWindow), true, "Fight grace window", SettingDescriptions.FightGraceWindow);
        category.CreateEntry(nameof(AiStandIn), true, "AI stand-in", SettingDescriptions.AiStandIn);
        category.CreateEntry(nameof(NurseRealScreen), true, "Real Nurse screen", SettingDescriptions.NurseRealScreen);
        category.CreateEntry(nameof(NativeLobby), true, "Native lobby", SettingDescriptions.NativeLobby);
        category.CreateEntry(nameof(OnlineInput), nameof(OnlineInputChoice.Auto), "Online input", SettingDescriptions.OnlineInput);
        category.CreateEntry(nameof(CoopSlot), 1, "Co-op save slot", SettingDescriptions.CoopSlot);
        category.CreateEntry(nameof(AllowDirectConnections), false, "Allow direct connections", SettingDescriptions.AllowDirectConnections);

        Developer(category, nameof(DevTools), false, "Developer test keys", SettingDescriptions.DevTools);
        Developer(category, nameof(ShowStatusBox), false, "Developer status box", SettingDescriptions.ShowStatusBox);
        Developer(category, nameof(SnapshotMeasure), false, "Measure replay snapshots", "Developer: log replay snapshot sizes and rates every 10 s and save the first 10 s of the session. Online play doesn't need it.");
        Developer(category, nameof(RecordOnlineMatches), false, "Record online matches", "Developer: when hosting, also record each match streamed to friends to UserData/T2TOnline/dev/streams (about 10 MB per match, the newest 20 and at most 250 MB kept), for F2 and the analysis tools. F3 recordings are written either way.");
        Developer(category, nameof(AllowUntestedBuild), false, "Allow an untested game build", "Developer: on a game build T2T Online wasn't tested on, run the offline test tools anyway. Online stays off.");
        Developer(category, nameof(LoopbackDelayMs), 0, "Loopback delay (ms)", "M0 test: one-way delay added to the F8 controller loopback, 0 to 1000.");
        Developer(category, nameof(PlaybackLinkDelayMs), 0, "F4 playback: link delay (ms)", "M2 test: one-way delay of the simulated network F4 playback runs snapshots through, 0 to 500.");
        Developer(category, nameof(PlaybackLinkJitterMs), 0, "F4 playback: link jitter (ms)", "M2 test: extra random delay per snapshot, 0 to this, 0 to 200.");
        Developer(category, nameof(PlaybackLinkLossPercent), 0, "F4 playback: link loss (%)", "M2 test: share of snapshots lost on the simulated link, 0 to 50.");
        Developer(category, nameof(SimulatedLagMs), 0, "Online test: simulated lag (ms)", "Steam adds this to everything this PC sends online, 0 to 300. Leave at 0 to play normally.");
        Developer(category, nameof(SimulatedJitterMs), 0, "Online test: simulated jitter (ms)", "Mean extra random delay on what this PC sends, 0 to 100. Leave at 0 to play normally.");
        Developer(category, nameof(SimulatedLossPercent), 0, "Online test: simulated loss (%)", "Share of this PC's packets Steam drops, 0 to 20. Leave at 0 to play normally.");
    }

    /// <summary>A developer's entry: hidden from settings screens, and written to the file only when not at its default.</summary>
    private static void Developer<T>(MelonPreferences_Category category, string name, T value, string displayName, string description) =>
        category.CreateEntry(name, value, displayName, description, is_hidden: true, dont_save_default: true);

    private static bool TryReadChoice(string? text, out OnlineInputChoice choice) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out choice) && Enum.IsDefined(choice);
}
