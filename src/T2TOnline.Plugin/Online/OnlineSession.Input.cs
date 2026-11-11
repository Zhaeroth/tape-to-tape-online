#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using Il2CppBishopGames.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Online;

// Keyboard-and-mouse friends and every friend's own controls. Host: a virtual keyboard or pad per friend, of
// the kind their hello says (swapped in place when they come back with the other), its maps rebuilt from their controls,
// their brawl combo relayed. Friend: what we play with (the OnlineInput setting, at the hello and again at each BeginPlay),
// our controls read from our own maps and sent after every welcome, our keyboard taken from this game's players from
// BeginPlay to EndPlay and read for the host. Apart from what the session does (OnlineSession.cs) and its words
// (OnlineSession.Text.cs).
internal sealed partial class OnlineSession
{
    /// <summary>Bindings of a friend's that aren't the game's defaults, named one by one in the log.</summary>
    private const int DifferencesNamed = 10;

    private LocalKeyboard? _localKeyboard;
    private InputBindingSet? _controls;
    private byte _controlsGeneration;
    private string _inputWhy = string.Empty;

    /// <summary>
    /// Friend: what we play with, by the OnlineInput setting (Auto: a gamepad if one is connected, else the keyboard and
    /// mouse), with our controls read. False, with a notice, when there's nothing to play with.
    /// </summary>
    private bool ChooseInput(out LocalPad? pad, out LocalKeyboard? keyboard)
    {
        OnlineInputChoice choice = ModSettings.OnlineInput;
        pad = choice == OnlineInputChoice.KeyboardMouse ? null : LocalPad.Find();
        keyboard = null;
        bool useKeyboard = choice == OnlineInputChoice.KeyboardMouse || (choice == OnlineInputChoice.Auto && pad == null);
        _inputWhy = choice switch
        {
            OnlineInputChoice.KeyboardMouse => "the OnlineInput setting is KeyboardMouse",
            OnlineInputChoice.Gamepad => "the OnlineInput setting is Gamepad",
            _ => pad == null ? "no gamepad is connected; OnlineInput is Auto" : "a gamepad is connected; OnlineInput is Auto",
        };
        if (!useKeyboard)
        {
            if (pad == null)
            {
                ModLog.Warn("Online: no connected gamepad that Rewired recognizes as a gamepad, and the OnlineInput setting is Gamepad. Plug one in and try again " +
                    $"(or set {Core.OnlineSettings.PlayersBuild.PlayOnlineWith} to Auto or Keyboard to play with the keyboard).");
                Notify(NoticeId.NoGamepad);
                return false;
            }

            return true;
        }

        pad = null;
        keyboard = LocalKeyboard.Find(NextControlsGeneration(), out string problem);
        if (keyboard == null)
        {
            ModLog.Warn($"Online: can't play with the keyboard: {problem}.");
            Notify(NoticeId.NoKeyboard);
            return false;
        }

        return true;
    }

    /// <summary>The next generation for our controls (1 to 255, then round again): each read is its own (a session's, a remap's, a pad swap's).</summary>
    private byte NextControlsGeneration() => _controlsGeneration = OwnMenuFlow.NextGeneration(_controlsGeneration);

    /// <summary>Friend: what our hello says we play with, and its device type.</summary>
    private (InputKind Kind, byte Device) FriendInput() => _localKeyboard != null
        ? (InputKind.KeyboardMouse, (byte)EInputDeviceType.MouseKeyboard)
        : (InputKind.Gamepad, (byte)(_localPad?.DeviceType ?? EInputDeviceType.GamepadXbox)); // no pad: the check tour's stream (OnlineSession.OnePc.cs)

    /// <summary>
    /// Friend, every input update while their keyboard is with the host's game: it stays off this game's players (not in the
    /// one-PC test, where the host's keyboard player is this game's too), and its key table goes to the host.
    /// </summary>
    private void SendKeyboard(LocalKeyboard keyboard, FriendSession friend, long now)
    {
        if (!_solo)
        {
            int takenFrom = keyboard.KeepDetached();
            if (takenFrom >= 0)
            {
                NoteTakenBack(takenFrom, "keyboard or mouse");
            }
        }

        if (_friendTransport is not { Cut: true })
        {
            (ulong held, ulong pressed) = keyboard.Read();
            MySettingsKeys(ref held, ref pressed); // My Settings' key or click, kept from the host (OnlineSession.MySettings.cs)
            friend.SendKeyboard(now, held, pressed);
            NoteOwnBrawlKeys(held, pressed, now); // in the host's brawl: its line's presses (OnlineSession.Brawl.cs)
        }
    }

    /// <summary>
    /// Host: a virtual controller of the kind <paramref name="hello"/> says (a keyboard for a keyboard friend, else a pad of
    /// their family), on <paramref name="preferredPlayerId"/> if free (or in <paramref name="replacing"/>'s place).
    /// </summary>
    private static VirtualController? CreateController(in HelloMessage hello, string tag, int preferredPlayerId, IReadOnlyCollection<int>? categories, VirtualController? replacing, out string problem) =>
        hello.Input == InputKind.KeyboardMouse
            ? VirtualKeyboard.Create(tag, preferredPlayerId, categories, replacing, out problem)
            : VirtualPad.Create(tag, VirtualPad.GamepadTypeFrom(hello.DeviceType), preferredPlayerId, categories, replacing, out problem);

    /// <summary>
    /// Host: a friend back (rejoined, or returning to a parked seat) with <paramref name="hello"/>: another pad family is
    /// taken as it is; the other kind of input (a keyboard after a pad, or back) swaps their virtual controller in place,
    /// on the same Rewired player, so they're the same game player with the map categories they had on.
    /// </summary>
    private static void MatchInput(RemotePad remote, in HelloMessage hello, string tag)
    {
        bool keyboard = hello.Input == InputKind.KeyboardMouse;
        if (remote.Pad.IsKeyboard == keyboard)
        {
            if (remote.Pad is VirtualPad pad)
            {
                pad.SetDeviceType(VirtualPad.GamepadTypeFrom(hello.DeviceType)); // they may have come back with another pad
            }

            return;
        }

        VirtualController old = remote.Pad;
        IReadOnlyCollection<int>? categories = remote.GamePlayer >= 0 ? old.CurrentEnabledCategories() : null;
        VirtualController? next;
        string problem;
        try
        {
            next = CreateController(hello, $"T2TOnline net:{tag}", old.RewiredPlayerId, categories, old, out problem);
        }
        catch (Exception ex)
        {
            next = null;
            problem = ModLog.SafeErrorText(ex);
            ModLog.Error("Online: making a friend's other kind of virtual controller threw", ex);
        }

        if (next == null)
        {
            ModLog.Warn($"Online: {tag} is back with their {(keyboard ? "keyboard" : "pad")}, but no {(keyboard ? "virtual keyboard" : "virtual pad")} could be made " +
                $"for them ({problem}); their {old.Kind.ToLowerInvariant()} stays on Rewired player {old.RewiredPlayerId}, standing still.");
            return;
        }

        old.Dispose();
        remote.Pad = next;
        remote.Combo.Hold(); // as the new controller's own buttons: what's down now counts once let go
        ModLog.Info($"Online: {tag} is back with their {(keyboard ? "keyboard" : "pad")}: a {next.Kind.ToLowerInvariant()} took their {old.Kind.ToLowerInvariant()}'s place on " +
            $"Rewired player {next.RewiredPlayerId}{(remote.GamePlayer >= 0 ? $" (game player {remote.GamePlayer}, map categories [{string.Join(",", categories!)}] on)" : ", waiting to join")}.");
        NoteMapsBuilt(remote, $"made in their {old.Kind.ToLowerInvariant()}'s place ({(keyboard ? "empty until their keyboard controls arrive" : "the game's gamepad maps until their pad controls arrive")})");
    }

    /// <summary>
    /// Host: a friend's controls arrived (<see cref="HostEventKind.BindingsChanged"/>): their virtual controller's maps are
    /// rebuilt from them, and the log says what they are and how they differ from the game's defaults.
    /// </summary>
    private void ApplyControls(ulong steamId, string tag)
    {
        if (!_pads.TryGetValue(steamId, out RemotePad? remote) || _host?.Find(steamId) is not { Bindings: { } controls } friend)
        {
            return;
        }

        string origin = controls.Source == BindingSource.OwnMaps ? "their own maps" : "the game's defaults";
        // Another generation than the one taken before while they're here: they changed them mid-session (their own remapper
        // while joined, or another pad); a rejoin sends the same generation again.
        string changed = remote.ControlsGeneration != 0 && remote.ControlsGeneration != controls.Generation ? ", changed mid-session" : string.Empty;
        if (controls.SameControlsAs(remote.Pad.Controls) || (controls.KeepsTemplateMaps && remote.Pad is VirtualPad && remote.Pad.Controls == null))
        {
            remote.ControlsGeneration = controls.Generation;
            ModLog.Info(controls.KeepsTemplateMaps
                ? $"Online: {tag}'s controls (generation {controls.Generation}{changed}): the game's defaults; their virtual pad keeps the game's gamepad maps."
                : $"Online: {tag}'s controls (generation {controls.Generation}, {origin}{changed}) are the ones their {remote.Pad.Kind.ToLowerInvariant()} has already.");
            return;
        }

        MapsBuilt built;
        if (remote.Pad is VirtualKeyboard keyboard && controls.Device == InputKind.KeyboardMouse)
        {
            built = keyboard.ApplyControls(controls);
        }
        else if (remote.Pad is VirtualPad pad && controls.Device == InputKind.Gamepad)
        {
            built = pad.ApplyControls(controls);
        }
        else
        {
            // Their stream's kind changed with a hello that raised no Rejoined (too soon after the last): catch up now.
            MatchInput(remote, friend.Hello, tag);
            if (remote.Pad.IsKeyboard != (controls.Device == InputKind.KeyboardMouse))
            {
                ModLog.Warn($"Online: {tag}'s controls are for a {(controls.Device == InputKind.KeyboardMouse ? "keyboard" : "pad")}, but they have a {remote.Pad.Kind.ToLowerInvariant()} here; not applied.");
                return;
            }

            ApplyControls(steamId, tag);
            return;
        }

        remote.ControlsGeneration = controls.Generation;
        string kind = controls.Device == InputKind.KeyboardMouse ? "keyboard and mouse" : "pad";
        string table = controls.Device == InputKind.KeyboardMouse
            ? string.Create(CultureInfo.InvariantCulture, $"{controls.Elements.Count(e => e.Kind == BindingElementKind.Key)} keys, {controls.Elements.Count(e => e.Kind == BindingElementKind.MouseButton)} mouse buttons, ")
            : string.Empty;
        ModLog.Info(controls.KeepsTemplateMaps
            ? $"Online: {tag}'s controls (generation {controls.Generation}{changed}): the game's defaults; their virtual pad has the game's gamepad maps again ({built.Created} bindings)."
            : string.Create(CultureInfo.InvariantCulture,
                $"Online: {tag}'s controls (generation {controls.Generation}, {kind}, {origin}{changed}): {table}{built.Created} bindings ({built.PerCategory}); ") +
              $"{controls.Differences} differ from the game's defaults (as they counted); {built.Refused} refused, {built.LeftOut} left out.");
        NoteMapsBuilt(remote, string.Create(CultureInfo.InvariantCulture, $"generation {controls.Generation} applied (") +
            (controls.KeepsTemplateMaps ? "the game's defaults: the game's gamepad maps" : $"{kind}, {origin}") + $"{changed})");
        NoteBrawlKeys(tag, controls);
        if (controls.Source == BindingSource.OwnMaps && !controls.KeepsTemplateMaps)
        {
            LogDifferences(tag, controls);
        }
    }

    /// <summary>Host: up to <see cref="DifferencesNamed"/> of a friend's bindings that this build's defaults don't have, by name.</summary>
    private static void LogDifferences(string tag, InputBindingSet controls)
    {
        IEnumerable<int> categories = controls.Device == InputKind.KeyboardMouse ? VirtualKeyboardDefinition.MapCategoryIds : VirtualPadDefinition.MapCategoryIds;
        List<InputBinding> defaults = controls.Device == InputKind.KeyboardMouse
            ? RewiredBindings.KeyboardDefaults(categories, out _)
            : RewiredBindings.PadDefaults(categories, out _);

        // The template's defaults and a pad's own maps write a trigger differently (InputBinding.AsInput): compare inputs.
        List<InputBinding> differ = InputBindingSet.NotIn(controls.Bindings.Select(b => b.AsInput).ToList(), defaults.Select(b => b.AsInput).ToList(), DifferencesNamed);
        if (differ.Count > 0)
        {
            ModLog.Info($"Online: {tag}'s bindings the game's defaults don't have{(differ.Count == DifferencesNamed ? $" (the first {DifferencesNamed})" : string.Empty)}: " +
                string.Join("; ", differ.Select(RewiredBindings.Describe)) + ".");
        }
    }

    /// <summary>
    /// Host, every input update: a keyboard friend's played frame onto their virtual keyboard (neutral while the AI plays
    /// for them), and their brawl combo (Q+E) relayed.
    /// </summary>
    private void FeedKeyboard(RemoteFriend friend, RemotePad remote, VirtualKeyboard keyboard, long now)
    {
        (KeyboardMouseState state, ulong taps) = friend.Keyboard!.Update(now);
        if (!friend.PadConnected || LobbyHolds(friend, remote, state.Held != 0) || _standIn.IsStandingIn(keyboard.RewiredPlayerId))
        {
            // As for a pad: away (their own remapper or settings open), no game starting, or the AI plays their skater; a key
            // they hold through the hand-back counts once let go, for the brawl combo too.
            keyboard.ApplyNeutral();
            remote.Combo.Hold();
            return;
        }

        keyboard.Apply(state, taps);
        NoteBrawlKeys(keyboard.RewiredPlayerId, state.Held, taps, now); // in a brawl: its line's presses (OnlineSession.Brawl.cs)
        RelayBrawlCombo(remote, state.Held, taps, BrawlCombo.KeyboardCombo(friend.Bindings), "Q+E");
    }

    /// <summary>
    /// Host: a friend's combo (LB+RB on a pad, Q+E on a keyboard: <paramref name="combo"/> over their played input) pressed
    /// just now accepts an open brawl offer for them (<see cref="BrawlComboRelay"/>). Called only while their controller takes
    /// their input; while it's held neutral, <see cref="BrawlComboWatch.Hold"/> instead.
    /// </summary>
    private static void RelayBrawlCombo(RemotePad remote, ulong held, ulong taps, ulong combo, string comboName)
    {
        if (remote.Combo.Next(held, taps, combo))
        {
            BrawlComboRelay.TryAccept($"{remote.Tag} (Rewired player {remote.Pad.RewiredPlayerId}, game player {remote.GamePlayer})", comboName);
        }
    }

    /// <summary>How a friend's virtual controller is named in the status box: their pad's family, or "keyboard".</summary>
    private static string DeviceLabel(VirtualController controller) =>
        controller.IsKeyboard ? "keyboard" : $"{VirtualPad.FamilyName(controller.DeviceType)} pad";
}
#endif
