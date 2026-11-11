#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Numerics;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// One virtual keyboard: a Rewired custom controller of <see cref="VirtualKeyboardDefinition"/> type ("T2TOnline
/// Keyboard"), given to a free Rewired player, whose button <c>i</c> is element <c>i</c> of a keyboard friend's key table.
/// Its maps are the friend's own keyboard and mouse maps, every category, rebuilt from their controls
/// (<see cref="ApplyControls"/>); until those arrive it has none and stands still. To the game it's a keyboard player for
/// button prompts and the controller screen (<see cref="VirtualPadCompat"/>), never a gamepad, and never the keyboard
/// that always drives the menus (D45: the host decides).
/// </summary>
internal sealed class VirtualKeyboard : VirtualController
{
    private ulong _set; // the buttons last set on the controller (values persist in Rewired until changed)
    private ulong _mustRelease;
    private bool _heldNeutral;

    private VirtualKeyboard(CustomController controller, Player player)
        : base(controller, player, EInputDeviceType.MouseKeyboard)
    {
    }

    /// <inheritdoc />
    public override string Kind => "Virtual keyboard";

    /// <summary>
    /// Creates a keyboard and gives it to a Rewired player with no controllers (<paramref name="preferredPlayerId"/> if
    /// free, else the lowest-numbered free one), or in <paramref name="replacing"/>'s place on its player (a friend back
    /// with the keyboard after playing with a pad: dispose the old one afterwards). With
    /// <paramref name="enabledCategories"/> null it starts in the game's activation mode ("press to join"). Returns null,
    /// with the reason, on failure.
    /// </summary>
    public static VirtualKeyboard? Create(string tag, int preferredPlayerId, IReadOnlyCollection<int>? enabledCategories, VirtualController? replacing, out string problem)
    {
        if (!VirtualKeyboardDefinition.Ensure(out problem))
        {
            return null;
        }

        Player? player = ChoosePlayer(preferredPlayerId, replacing);
        if (player == null)
        {
            problem = "no Rewired player is free";
            return null;
        }

        CustomController? controller = ReInput.controllers.CreateCustomController(VirtualKeyboardDefinition.SourceId, tag);
        if (controller == null)
        {
            problem = "Rewired did not create the custom controller";
            return null;
        }

        var keyboard = new VirtualKeyboard(controller, player);
        try
        {
            keyboard.Attach(replacing);
            keyboard.LoadMaps(VirtualKeyboardDefinition.MapCategoryIds, enabledCategories);
            if (enabledCategories == null)
            {
                RewiredPlayerInputAssigner.SetControlsToActivationMode(player.id);
            }

            ModLog.Info($"Virtual keyboard: created on Rewired player {player.id} ({player.name}), its maps empty until the friend's controls arrive; " +
                (enabledCategories == null ? "waiting to join" : $"map categories [{string.Join(",", enabledCategories)}] on, as the controller it replaces had them") +
                $". Rewired won't auto-assign a pad to that player while it's there (it was {keyboard.ExclusionBefore}).");
            return keyboard;
        }
        catch
        {
            keyboard.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the keyboard's maps from a keyboard friend's controls: each category's map is emptied and filled with
    /// their bindings there, key-table element <c>i</c> on button <c>i</c> (a key on an axis action drives its side of
    /// the axis, as on the couch; the game's input behaviours don't simulate digital axes). Whether a map is on stays.
    /// Bindings of "UI Mouse Click" are left out (<see cref="RewiredBindings.SkipsAction"/>).
    /// </summary>
    public MapsBuilt ApplyControls(InputBindingSet controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        MapsBuilt built = Fill(controls);
        Controls = controls;
        ApplyNeutral(); // a new table: nothing held from the old one, and what's down counts once let go
        if (built.LeftOut > 0)
        {
            ModLog.Info($"Virtual keyboard on Rewired player {Player.id}: {built.LeftOut} binding(s) of \"{RewiredBindings.MouseClickActionName}\" left out (the game's Fighting maps): the game's UI reader asks " +
                "a player for its mouse on that action and throws without one, and the fighter never reads it.");
        }

        return built;
    }

    /// <inheritdoc />
    /// <remarks>Before the friend's controls arrive, every map is left empty. Nothing held changes: the key table is the same.</remarks>
    protected override void RebuildMaps()
    {
        if (Controls != null)
        {
            Fill(Controls);
            return;
        }

        foreach (int category in VirtualKeyboardDefinition.MapCategoryIds)
        {
            if (MapIn(category) is { } map)
            {
                RewiredBindings.Clear(map);
            }
        }
    }

    /// <summary>Empties every category's map and fills it with <paramref name="controls"/>' bindings there (see <see cref="ApplyControls"/>).</summary>
    private MapsBuilt Fill(InputBindingSet controls)
    {
        int created = 0;
        int refused = 0;
        int leftOut = 0;
        var made = new List<InputBinding>();
        foreach (int category in VirtualKeyboardDefinition.MapCategoryIds)
        {
            ControllerMap? map = MapIn(category);
            if (map == null)
            {
                ModLog.Info($"Virtual keyboard: map category {category}: no map, left empty.");
                continue;
            }

            RewiredBindings.Clear(map);
            foreach (InputBinding binding in controls.Bindings)
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

                int element = controls.IndexOf(binding.Element);
                if (element < 0 || element >= VirtualKeyboardDefinition.ButtonElementIds.Length || ReInput.mapping.GetAction(binding.ActionId) == null ||
                    !map.CreateElementMap(binding.ActionId, binding.Negative ? Pole.Negative : Pole.Positive, VirtualKeyboardDefinition.ButtonElementIds[element],
                        ControllerElementType.Button, AxisRange.Full, false))
                {
                    refused++;
                    continue;
                }

                created++;
                made.Add(binding);
            }
        }

        foreach (InputBinding binding in controls.Bindings)
        {
            refused += Array.IndexOf(VirtualKeyboardDefinition.MapCategoryIds, (int)binding.Category) < 0 ? 1 : 0;
        }

        return new MapsBuilt(created, refused, leftOut, RewiredBindings.PerCategory(made));
    }

    /// <summary>
    /// Sets the buttons: element <c>i</c> of <paramref name="state"/> held, or tapped (<paramref name="taps"/>, down for
    /// this update only). Only buttons that changed are set (Rewired keeps a custom controller's values until changed).
    /// After <see cref="ApplyNeutral"/>, a key already down counts only once it's been let go.
    /// </summary>
    public void Apply(in KeyboardMouseState state, ulong taps)
    {
        ulong down = state.Held | taps;
        if (_heldNeutral)
        {
            _heldNeutral = false;
            _mustRelease = down;
        }

        _mustRelease &= down; // a key seen up is free again
        down &= ~_mustRelease;
        SetButtons(down);
    }

    /// <summary>Releases every button; what the friend holds when it's theirs again counts once let go.</summary>
    public override void ApplyNeutral()
    {
        SetButtons(0);
        _heldNeutral = true;
    }

    private void SetButtons(ulong down)
    {
        ulong changed = down ^ _set;
        while (changed != 0)
        {
            int button = BitOperations.TrailingZeroCount(changed);
            changed &= changed - 1;
            Controller.SetButtonValue(button, (down & (1UL << button)) != 0);
        }

        _set = down;
    }
}
#endif
