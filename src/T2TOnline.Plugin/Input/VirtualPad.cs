#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>How a virtual controller's maps were built: bindings made, refused, and left out on purpose.</summary>
/// <param name="Created">Element maps Rewired made.</param>
/// <param name="Refused">Bindings Rewired refused, or for a category or action this build doesn't have, or an element the controller lacks.</param>
/// <param name="LeftOut">Bindings of "UI Mouse Click", left out (<see cref="RewiredBindings.SkipsAction"/>).</param>
/// <param name="PerCategory">Bindings made per category, for the log.</param>
internal readonly record struct MapsBuilt(int Created, int Refused, int LeftOut, string PerCategory);

/// <summary>
/// One virtual gamepad: a Rewired custom controller of <see cref="VirtualPadDefinition"/> type, given to a free
/// Rewired player and bound exactly like a real gamepad: from the game's gamepad-template maps, or from the friend's own
/// bindings when they remapped (<see cref="ApplyControls"/>). To the game it's a plugged-in pad in activation mode;
/// pressing the activate button joins it through the game's own join code.
/// </summary>
internal sealed class VirtualPad : VirtualController
{
    private PadButtons _mustRelease;
    private bool _heldNeutral;

    private VirtualPad(CustomController controller, Player player, EInputDeviceType deviceType)
        : base(controller, player, deviceType)
    {
    }

    /// <inheritdoc />
    public override string Kind => "Virtual pad";

    /// <summary>
    /// The pad family a peer claims (a friend's hello): a gamepad family, or Xbox for anything else (anything else from the
    /// network would make the game treat the pad as a keyboard).
    /// </summary>
    public static EInputDeviceType GamepadTypeFrom(byte claimed) =>
        (EInputDeviceType)claimed is >= EInputDeviceType.GamepadXbox and <= EInputDeviceType.GamepadGeneric ? (EInputDeviceType)claimed : EInputDeviceType.GamepadXbox;

    /// <summary>A pad family as players know it ("Xbox", "PlayStation"), for the log and the status box.</summary>
    public static string FamilyName(EInputDeviceType family) => family switch
    {
        EInputDeviceType.GamepadXbox => "Xbox",
        EInputDeviceType.GamepadDualShock => "PlayStation",
        EInputDeviceType.GamepadNintendo => "Nintendo",
        EInputDeviceType.GamepadGeneric => "a generic pad",
        EInputDeviceType.MouseKeyboard => "keyboard and mouse",
        _ => "an unknown kind of pad",
    };

    /// <summary>
    /// Creates a pad and gives it to a Rewired player with no controllers: <paramref name="preferredPlayerId"/> if that
    /// one is free, otherwise the lowest-numbered free one (player 0 is the keyboard's). Returns null, with the reason,
    /// on failure.
    /// </summary>
    /// <param name="tag">Rewired tag for the controller.</param>
    /// <param name="deviceType">The pad family the game should treat it as (button prompts, gamepad checks).</param>
    /// <param name="preferredPlayerId">The Rewired player to use if free, or -1.</param>
    /// <param name="enabledCategories">
    /// Map categories to start enabled, when the pad takes over a player the game has already joined; null for a new
    /// pad, which starts in the game's activation mode ("press to join").
    /// </param>
    /// <param name="problem">Why creation failed.</param>
    public static VirtualPad? Create(string tag, EInputDeviceType deviceType, int preferredPlayerId, IReadOnlyCollection<int>? enabledCategories, out string problem) =>
        Create(tag, deviceType, preferredPlayerId, enabledCategories, null, out problem);

    /// <summary>
    /// As <see cref="Create(string, EInputDeviceType, int, IReadOnlyCollection{int}?, out string)"/>; with
    /// <paramref name="replacing"/>, the pad takes that controller's place on its player (a friend back with a pad after
    /// playing with the keyboard): dispose the old one afterwards.
    /// </summary>
    public static VirtualPad? Create(string tag, EInputDeviceType deviceType, int preferredPlayerId, IReadOnlyCollection<int>? enabledCategories, VirtualController? replacing, out string problem)
    {
        if (!VirtualPadDefinition.Ensure(out problem))
        {
            return null;
        }

        Player? player = ChoosePlayer(preferredPlayerId, replacing);
        if (player == null)
        {
            problem = "no Rewired player is free";
            return null;
        }

        CustomController? controller = ReInput.controllers.CreateCustomController(VirtualPadDefinition.SourceId, tag);
        if (controller == null)
        {
            problem = "Rewired did not create the custom controller";
            return null;
        }

        var pad = new VirtualPad(controller, player, deviceType);
        try
        {
            pad.Attach(replacing);
            pad.LoadMaps(VirtualPadDefinition.MapCategoryIds, enabledCategories);
            MapsBuilt built = pad.Fill(DefaultBindings(out int lacking));
            if (enabledCategories == null)
            {
                RewiredPlayerInputAssigner.SetControlsToActivationMode(player.id);
            }

            ModLog.Info($"Virtual pad: created on Rewired player {player.id} ({player.name}) as {deviceType}, with {built.Created} bindings copied from the game's gamepad maps; " +
                (enabledCategories == null ? "waiting to join" : $"map categories [{string.Join(",", enabledCategories)}] on, as the {(replacing != null ? "controller it replaces" : "real pad")} had them") +
                $". Rewired won't auto-assign a pad to that player while it's there (it was {pad.ExclusionBefore}).");
            LogLeftOut(pad.Kind, player.id, built, lacking);
            return pad;
        }
        catch
        {
            pad.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets every element of the pad. Rewired picks the values up on its next input update. After
    /// <see cref="ApplyNeutral"/>, a button already down counts only once it's been let go.
    /// </summary>
    public void Apply(in PadState state, PadButtons extraPresses)
    {
        for (int i = 0; i < PadState.AxisCount; i++)
        {
            Controller.SetAxisValue(i, state.GetAxis((PadAxis)i));
        }

        PadButtons down = state.Buttons | extraPresses;
        if (_heldNeutral)
        {
            _heldNeutral = false;
            _mustRelease = down;
        }

        _mustRelease &= down; // a button seen up is free again
        down &= ~_mustRelease;
        for (int i = 0; i < PadState.ButtonCount; i++)
        {
            Controller.SetButtonValue(i, ((int)down & (1 << i)) != 0);
        }
    }

    /// <summary>
    /// Holds the pad neutral (its friend dropped or unplugged their pad, or the AI plays for them). When it's theirs again,
    /// a button they're already holding counts only once they've let it go: the first state applied would otherwise be a
    /// fresh press, a shot or a check the moment their skater is handed back. Axes follow at once.
    /// </summary>
    public override void ApplyNeutral()
    {
        Apply(PadState.Neutral, PadButtons.None);
        _heldNeutral = true;
    }

    /// <summary>
    /// The pad family the game treats the pad as from now on (a friend can come back with another pad). Returns true, and
    /// logs it, when that changed.
    /// </summary>
    public bool SetDeviceType(EInputDeviceType deviceType)
    {
        if (deviceType == DeviceType || IsDisposed)
        {
            return false;
        }

        ModLog.Info($"Virtual pad on Rewired player {Player.id}: button prompts now for {deviceType} (was {DeviceType}).");
        DeviceType = deviceType;
        VirtualPadCompat.Register(Player.id, Controller.id, deviceType);
        return true;
    }

    /// <summary>
    /// Rebuilds the pad's maps from a friend's controls: their own bindings (in gamepad-template terms), or the game's
    /// template maps when they're on the defaults. Each category's map is emptied and filled again; whether it's on stays.
    /// </summary>
    public MapsBuilt ApplyControls(InputBindingSet controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        int lacking = 0;
        MapsBuilt built = Fill(controls.KeepsTemplateMaps
            ? DefaultBindings(out lacking)
            : controls.Bindings);
        Controls = controls.KeepsTemplateMaps ? null : controls;
        LogLeftOut(Kind, Player.id, built, lacking);
        return built;
    }

    /// <inheritdoc />
    protected override void RebuildMaps() => Fill(Controls != null ? Controls.Bindings : DefaultBindings(out _));

    /// <summary>
    /// Empties the pad's map in every category and fills it with <paramref name="bindings"/>, element by element. Bindings
    /// of "UI Mouse Click" are left out (<see cref="RewiredBindings.SkipsAction"/>).
    /// </summary>
    private MapsBuilt Fill(IReadOnlyCollection<InputBinding> bindings)
    {
        int created = 0;
        int refused = 0;
        int leftOut = 0;
        var made = new List<InputBinding>();
        foreach (int category in VirtualPadDefinition.MapCategoryIds)
        {
            ControllerMap? map = MapIn(category);
            if (map == null)
            {
                ModLog.Info($"Virtual pad: map category {category}: no pad map, left empty.");
                refused += bindings.Count(b => b.Category == category);
                continue;
            }

            RewiredBindings.Clear(map);
            foreach (InputBinding binding in bindings)
            {
                if (binding.Category != category)
                {
                    continue;
                }

                if (RewiredBindings.SkipsAction(binding.ActionId))
                {
                    leftOut++;
                    continue;
                }

                bool isAxis = binding.Element.Kind == BindingElementKind.PadAxis;
                int[] elements = isAxis ? VirtualPadDefinition.AxisElementIds : VirtualPadDefinition.ButtonElementIds;
                if (binding.Element.IsKeyboard || binding.Element.Code >= elements.Length || ReInput.mapping.GetAction(binding.ActionId) == null ||
                    !map.CreateElementMap(binding.ActionId, binding.Negative ? Pole.Negative : Pole.Positive, elements[binding.Element.Code],
                        isAxis ? ControllerElementType.Axis : ControllerElementType.Button, isAxis ? (AxisRange)binding.Range : AxisRange.Full, isAxis && binding.Invert))
                {
                    refused++;
                    continue;
                }

                created++;
                made.Add(binding);
            }
        }

        refused += bindings.Count(b => Array.IndexOf(VirtualPadDefinition.MapCategoryIds, (int)b.Category) < 0);
        return new MapsBuilt(created, refused, leftOut, RewiredBindings.PerCategory(made));
    }

    /// <summary>
    /// The game's gamepad template bindings for a virtual pad, and how many it can't carry; those are named once per launch
    /// (the same for every virtual pad): a game action only on such an element (the Guide button, a six-button pad's third
    /// face button) is one no friend can give on a standard pad.
    /// </summary>
    private static List<InputBinding> DefaultBindings(out int lacking)
    {
        List<string>? names = _lackingNamed ? null : new List<string>();
        List<InputBinding> bindings = RewiredBindings.PadDefaults(VirtualPadDefinition.MapCategoryIds, out lacking, names);
        if (names != null)
        {
            _lackingNamed = true;
            ModLog.Info(names.Count == 0
                ? "Virtual pad: every binding of the game's gamepad maps is on an element a friend's pad sends."
                : $"Virtual pad: the game's gamepad maps bind {names.Count} action(s) to elements a friend's pad doesn't send (a standard pad's 14 buttons and " +
                  $"6 axes go over the network), left out: {string.Join("; ", names)}.");
        }

        return bindings;
    }

    private static bool _lackingNamed;

    /// <summary>
    /// At start-up, on every PC (after the Rewired report): the game's gamepad bindings a friend's pad can't send (the Guide
    /// button, a six-button pad's third face button), named once; before, they were named only when the first virtual pad
    /// was built, so a session of keyboard friends, or a friend's PC, never said. Never throws.
    /// </summary>
    public static void NameLackingAtStartUp()
    {
        if (_lackingNamed)
        {
            return;
        }

        try
        {
            var names = new List<string>();
            RewiredBindings.PadDefaults(VirtualPadDefinition.MapCategoryIds, out _, names);
            _lackingNamed = true;
            ModLog.Info(names.Count == 0
                ? "Rewired: every binding of the game's gamepad maps is on an element a friend's pad sends."
                : $"Rewired: the game's gamepad maps bind {names.Count} action(s) to elements a friend's pad doesn't send (a standard pad's 14 buttons and 6 axes go " +
                  $"over the network): {string.Join("; ", names)}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Rewired: the game's gamepad bindings a friend's pad can't send couldn't be listed ({ModLog.SafeErrorText(ex)}); the first virtual pad lists them.");
        }
    }

    private static void LogLeftOut(string kind, int rewiredPlayerId, MapsBuilt built, int lacking)
    {
        if (built.Refused + lacking > 0)
        {
            ModLog.Info($"{kind} on Rewired player {rewiredPlayerId}: {built.Refused + lacking} bindings skipped (elements a standard pad lacks, an action or category this build doesn't have, or rejected by Rewired).");
        }

        if (built.LeftOut > 0)
        {
            ModLog.Info($"{kind} on Rewired player {rewiredPlayerId}: {built.LeftOut} binding(s) of \"{RewiredBindings.MouseClickActionName}\" left out (the game's Fighting maps): the game's UI reader asks " +
                "a player for its mouse on that action and throws without one, and the fighter never reads it.");
        }
    }
}
#endif
