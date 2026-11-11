#if HAS_INTEROP
using System;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Runtime;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// The native screens' layer (D139-D141): what a player needs whatever online's state, with online on, with online off on a tested
/// game build, and after the runtime stopped on an error. It owns the Settings menu's Online tab (<see cref="SettingsTab.Install"/>,
/// whose patch then serves every open), and ticks what the Backups screen and the game's prompts need: the held prefabs
/// (<see cref="UiAssets"/>), the shell the Backups screen opens in (<see cref="LobbyShell"/>) and the game's yes/no and notice
/// (<see cref="LobbyPrompts"/>); and gives the held prefabs back once the title has gone. The lobby's own patch, the Online group and the
/// lobby's screens stay the runtime's (<see cref="LobbyKit"/>): they need its session.
/// <para>
/// The plugin starts it once, on a game build the mod patches, before the runtime or the offline shell, and ticks it every frame after
/// them (<see cref="Update"/>). While the runtime runs, its kit ticks the layer at the kit's own point in the frame
/// (<see cref="TickFromKit"/>), so the lobby keeps the order it was proven in, and the plugin's tick that frame is skipped: one tick a
/// frame, whoever gives it, and none missed when the runtime stops. Each part is guarded on its own: ten failures with no clean half
/// minute between them turn that part off for the game, with one line, and the game's own screens carry on (<see cref="PartFailures"/>:
/// a part that looks only a few times a second is counted over the frames it idles through). Cheap when nothing is open: a few field
/// reads a frame, and one look at the title a second.
/// </para>
/// </summary>
internal static class NativeUiLayer
{
    private const long TitleCheckMs = 1_000;

    // The built-in parts, in the kit's own order: the held prefabs' loads, the shell (the Backups screen's too), the game's prompts, then giving the
    // prefabs back once the title has gone. Later screens add theirs (Add). Each delegate is made once here: nothing is allocated a frame.
    private static readonly System.Collections.Generic.List<Part> Parts = new()
    {
        new Part("the held prefabs", UiAssets.Tick),
        new Part("the screens' shell", LobbyShell.Tick),
        new Part("the game's prompts", LobbyPrompts.Tick),
        new Part("giving the held prefabs back", ReleaseWithoutTitle),
    };

    private static bool _kitTicked;
    private static long _titleCheckAt;
    private static OnlineSession? _session;

    /// <summary>True once the plugin started it (a game build the mod patches).</summary>
    public static bool Started { get; private set; }

    /// <summary>What the mod runs this session, for the Settings tab's rows.</summary>
    public static SettingsState State { get; private set; } = SettingsState.OnlineOff;

    /// <summary>Frames ticked so far (a check tour reads that the layer runs in its state).</summary>
    public static long Ticks { get; private set; }

    /// <summary>
    /// The runtime's online session while online is on, or null: with online off there is none, and after an error the one that stopped
    /// is no one's to act on. The Settings tab's rows read the session through this alone.
    /// </summary>
    public static OnlineSession? Session
    {
        get => State == SettingsState.OnlineOn ? _session : null;
        set => _session = value;
    }

    /// <summary>
    /// True when Steam refused relays alone on this PC (the session's marker, <see cref="RelayRefusal"/>), or the developer's offline start
    /// says so for <c>relay</c> (no marker written): Direct Connections shows.
    /// </summary>
    public static bool RelayRefused => RelayRefusal.Refused || DevOfflineStart.RelayRefused;

    /// <summary>
    /// Once, before the runtime or the offline shell starts: the Settings tab's patch (it logs its own line, and a failure leaves the game's
    /// own Settings menu), in <paramref name="state"/>. Never throws.
    /// </summary>
    public static void Start(HarmonyLib.Harmony harmony, SettingsState state)
    {
        if (Started)
        {
            return;
        }

        Started = true;
        State = state;
        try
        {
            SettingsTab.Install(harmony);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Native screens: the Settings menu's Online tab couldn't be installed ({ModLog.SafeErrorText(ex)}); the menu has the game's own tabs.");
            BugReports.HoldMakesToo("the Settings menu's Online tab couldn't be installed, and with it Problem Report");
        }

        // The problem report's prompt and its offer at the title (D140): parts of this frame in every state (never throws).
        ReportFlow.Install();
        ModLog.Info($"Native screens: on ({SettingsStates.Describe(state)}): the Settings menu's Online tab, the Backups screen and the game's prompts work whatever online's state.");
    }

    /// <summary>Not started this session (the plugin says why): the game's own screens only.</summary>
    public static void Skip(string why) =>
        ModLog.Info($"Native screens: off this session: {why}; the game's own screens only.");

    /// <summary>What the mod runs now changed (the offline shell started, or the runtime stopped after an error). Logged once a change.</summary>
    public static void SetState(SettingsState state)
    {
        if (state == State)
        {
            return;
        }

        State = state;
        if (Started)
        {
            ModLog.Info($"Native screens: now {SettingsStates.Describe(state)}; they stay on (the Settings menu's Online tab, the Backups screen and the game's prompts).");
        }
    }

    /// <summary>From the runtime's kit, at its point in the frame: the tick, whether or not the layer was started (the lobby's screens need it).</summary>
    public static void TickFromKit()
    {
        _kitTicked = true;
        Tick();
    }

    /// <summary>From the plugin every frame, after the runtime and the offline shell: the tick, unless the kit gave it this frame.</summary>
    public static void Update()
    {
        if (_kitTicked)
        {
            _kitTicked = false;
            return;
        }

        if (Started)
        {
            Tick();
        }
    }

    /// <summary>
    /// A part of a later screen that needs the layer's frame in every state (the report offer, the Backups screen's own steps): ticked after the
    /// built-in parts, in the order added, guarded as they are. Add each once, at start-up or before its first use; <paramref name="name"/> is
    /// how the log names it ("the report offer"). Throws only for a null argument.
    /// </summary>
    public static void Add(string name, Action tick)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tick);
        Parts.Add(new Part(name, tick));
    }

    private static void Tick()
    {
        Ticks++;
        for (int i = 0; i < Parts.Count; i++)
        {
            Part part = Parts[i];
            if (part.Failures.Off)
            {
                continue;
            }

            try
            {
                part.Tick();
                part.Failures.Succeeded(Ticks);
            }
            catch (Exception ex)
            {
                Failed(part, ex);
            }
        }
    }

    /// <summary>Held prefabs go back when the title has gone (no clone lives without it; a later request loads them again): one look a second.</summary>
    private static void ReleaseWithoutTitle()
    {
        long now = Environment.TickCount64;
        if (now < _titleCheckAt)
        {
            return;
        }

        _titleCheckAt = now + TitleCheckMs;
        if (!LobbyShell.IsOpen && !CampaignGame.TitleLoaded())
        {
            UiAssets.ReleaseAll("the title has gone");
        }
    }

    private static void Failed(Part part, Exception ex)
    {
        ModLog.Warn($"Native screens: {part.Name} failed ({ModLog.SafeErrorText(ex)}).");
        if (part.Failures.Failed(Ticks))
        {
            ModLog.Warn($"Native screens: {part.Name} failed {PartFailures.MaxFailures} times with no clean half minute between; it's off for this game, and the game's own screens carry on.");
        }
    }

    /// <summary>One part of the layer's frame, with its failures.</summary>
    private sealed class Part
    {
        public Part(string name, Action tick)
        {
            Name = name;
            Tick = tick;
        }

        public string Name { get; }

        public Action Tick { get; }

        public PartFailures Failures { get; } = new();
    }
}
#endif
