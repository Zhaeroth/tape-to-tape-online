#if HAS_INTEROP
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.NativeUi;
using T2TOnline.Plugin.Runtime;

namespace T2TOnline.Plugin.NativeLobby;

// The offline-start tour (run.txt's first line "offline reason=steam|relay|error"; Core's OfflineStart): the mod started as if online were
// off, or (error) stopped as after an error by this tour's first step, and the native screens' layer checked in that state. Nothing here
// presses a pad or opens a menu of the game's but one prompt of the mod's own, closed again; the walk of the screens comes after it.
internal static partial class LobbyUiCheck
{
    private const double StopSeconds = 5;
    private const double PromptFindSeconds = 9; // past LobbyPrompts' own 8 s, after which it takes the safe answer by itself

    private static void AddOfflineSteps()
    {
        Steps["off_state"] = StepOffState;
        Steps["off_settings"] = StepOffSettings;
        Steps["off_report"] = StepOffReport;
        Steps["off_backups"] = StepOffBackups;
        Steps["off_offer"] = StepOffOffer;
        Steps["off_notice"] = StepOffNotice;
    }

    /// <summary>The state the reason asks for; for error, the runtime's stop first, staged. Then the layer, the rows' context and a prompt's round trip.</summary>
    private static IEnumerable<Wait> StepOffState()
    {
        OfflineStartReason reason = DevOfflineStart.Reason;
        StepNote($"the developer's offline start: {(reason == OfflineStartReason.None ? "none" : OfflineStart.Name(reason))}; native screens {(NativeUiLayer.Started ? "started" : "not started")}, {SettingsStates.Describe(NativeUiLayer.State)}");
        if (reason == OfflineStartReason.None)
        {
            Fail("no developer's offline start this launch: run.txt's line must be \"offline reason=steam|relay|error\", with the developer tools on, on a tested build, and online not off already for another reason");
            yield break;
        }

        if (reason == OfflineStartReason.Error)
        {
            if (NativeUiLayer.State == SettingsState.OnlineOn)
            {
                StageErrors(1, "the developer's offline start (error): the runtime stops as after an error");
                if (!DevOfflineStart.AskStop())
                {
                    Fail("the runtime's stop couldn't be asked for (no tour holds, or the reason isn't error)");
                    yield break;
                }
            }

            yield return Until(() => NativeUiLayer.State == SettingsState.AfterError, StopSeconds, "the runtime stopped and the offline shell up");
            OffCheck("after the error", NativeUiLayer.State == SettingsState.AfterError && !T2TOnlinePlugin.OnlineAvailable,
                $"the runtime stopped, online off (it says: {T2TOnlinePlugin.OfflineReason})");
        }
        else
        {
            OffCheck("online off from start-up", NativeUiLayer.State == SettingsState.OnlineOff && !T2TOnlinePlugin.OnlineAvailable
                && T2TOnlinePlugin.OfflineReason == OfflineStart.OffReason(reason), $"online off for {OfflineStart.OffReason(reason)} (it says: {T2TOnlinePlugin.OfflineReason})");
            OffCheck("no Online group", !OnlineGroup.IsIn, "the title's own groups only (the Online group is the runtime's)");
        }

        OffCheck("the layer", NativeUiLayer.Started && SettingsTab.OffReason == null && NativeUiLayer.Session == null,
            $"started, the Settings tab's patch in (off: {SettingsTab.OffReason ?? "no"}), no session for the rows");

        SettingsContext context = TabRows.Context();
        bool direct = SettingsRows.Shown(SettingsRowId.DirectConnections, context);
        StepNote($"the rows' context: {SettingsStates.Describe(context.State)}, role {context.SessionRole}, at the title {context.AtTitle}, Direct Connections {(direct ? "shown" : "hidden")}, landing on {SettingsStates.Landing(context, SettingsRows.Order)?.ToString() ?? "none"}");
        OffCheck("no Leave or End", !SettingsRows.Shown(SettingsRowId.LeaveOnlineGame, context) && !SettingsRows.Shown(SettingsRowId.EndOnlineGame, context), "neither shown");
        if (reason == OfflineStartReason.Relay)
        {
            OffCheck("Direct Connections", direct, "shown: Steam refused relays alone, as the reason says");
        }

        long ticks = NativeUiLayer.Ticks;
        yield return Frames(5);
        OffCheck("the layer's tick", NativeUiLayer.Ticks > ticks, $"ticking ({NativeUiLayer.Ticks - ticks} ticks in 5 frames)");

        // A prompt of the mod's own, through the game's: found by the layer's tick, closed, and gone. Its words are any the mod has.
        bool asked = LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.Online.Key), LobbyLocalization.Say(LobbyWords.OnlineSettings.Key), LobbyLocalization.Say(LobbyTerms.Ok), static () => { });
        OffCheck("a prompt asked for", asked, "the game's notice on its way (none open before)");
        if (!asked)
        {
            yield break;
        }

        yield return Until(() => LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking, PromptFindSeconds, "the prompt found");
        bool found = LobbyPrompts.IsOpen && !LobbyPrompts.IsAsking;
        LobbyPrompts.CloseNow();
        yield return Until(() => !LobbyPrompts.IsOpen, PromptFindSeconds, "the prompt gone");
        OffCheck("the prompt's round trip", found && !LobbyPrompts.IsOpen, $"found ({found}) and closed through the layer's ticks");
        yield return Seconds(0.7);
    }

    private static void OffCheck(string what, bool ok, string wanted)
    {
        if (ok)
        {
            StepNote($"{what}: {wanted}");
        }
        else
        {
            Fail($"{what}: expected {wanted}");
        }
    }
}
#endif
