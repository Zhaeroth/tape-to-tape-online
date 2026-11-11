#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppRewired;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using Player = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// A pad friend's own controls, read before their pad is taken from their game's players: the maps their game has for it
/// (the player holding it, saved remaps included; Rewired player 1's saved or default maps for a pad nobody holds, where a
/// solo couch pad sits), in gamepad-template terms, so they fit whatever pad model the host's virtual pad stands in for.
/// The same as the game's defaults for that pad in every category: "game defaults", and the host keeps its template maps.
/// </summary>
internal static class PadRemapReader
{
    /// <summary>Player whose saved maps a pad nobody holds is read with.</summary>
    private const int SoloPadPlayer = 1;

    /// <summary>
    /// A pad's controls as its game has them, read without logging (the panel's controller check reads them too): its own
    /// bindings in gamepad-template terms (the maps of Rewired player <paramref name="ownerPlayerId"/>, who holds it, saved
    /// remaps included; else that player's, or player 1's, saved or default maps), the game's defaults for that pad, how many
    /// differ, and what its maps bind to elements a standard pad lacks. Throws what Rewired throws.
    /// </summary>
    public static PadControls ReadControls(Joystick joystick, int ownerPlayerId)
    {
        Player? owner = ownerPlayerId >= 0 ? ReInput.players.GetPlayer(ownerPlayerId) : null;
        int savedFor = owner?.id ?? SoloPadPlayer;
        var own = new List<InputBinding>();
        var defaults = new List<InputBinding>();
        int lacking = 0;
        var lackingNames = new List<string>();
        foreach (int category in RewiredBindings.CategoryIds())
        {
            ControllerMap? live = owner?.controllers.maps.GetMap(ControllerType.Joystick, joystick.id, category, 0) ??
                ReInput.mapping.GetJoystickMapInstanceSavedOrDefault(savedFor, joystick, category, 0);
            lacking += Add(own, category, joystick, live, lackingNames);
            Add(defaults, category, joystick, ReInput.mapping.GetJoystickMapInstance(joystick, category, 0), null);
        }

        string whose = owner != null ? $"Rewired player {owner.id}'s maps" : $"Rewired player {SoloPadPlayer}'s saved or default maps (no player holds the pad)";
        return new PadControls(own, defaults, InputBindingSet.CountDifferences(own, defaults), lacking, lackingNames, whose);
    }

    /// <summary>
    /// Our pad's controls (generation <paramref name="generation"/>), from the maps of the player that had it (or that it was
    /// lent to: <see cref="LocalPad.ControlsPlayerId"/>). Never throws: a failure is logged and reads as the game's defaults.
    /// </summary>
    public static InputBindingSet Read(LocalPad pad, byte generation)
    {
        try
        {
            (List<InputBinding> own, List<InputBinding> defaults, int differences, int lacking, List<string> lackingNames, string whose) =
                ReadControls(pad.Joystick, pad.ControlsPlayerId);
            if (lackingNames.Count > 0)
            {
                // What this pad's maps put on elements a standard pad doesn't have (a Steam Controller's grips, a Guide button):
                // those never reach the host, whose virtual pad has only a standard pad's 14 buttons and 6 axes.
                ModLog.Info($"Your pad's maps bind {lackingNames.Count} action(s) to elements the host's virtual pad doesn't have, left out: " +
                    $"{string.Join("; ", lackingNames.Take(12))}{(lackingNames.Count > 12 ? "; ..." : string.Empty)}.");
            }

            if (differences == 0)
            {
                ModLog.Info($"Your pad's controls: the game's defaults ({whose}, {own.Count} bindings; {lacking} on elements a standard pad lacks); the host keeps its own gamepad maps for you.");
                return InputBindingSet.PadDefaults(generation);
            }

            var set = new InputBindingSet(generation, InputKind.Gamepad, BindingSource.OwnMaps, (byte)Math.Min(differences, byte.MaxValue), Array.Empty<BindingElement>(), own);
            ModLog.Info($"Your pad's controls: your own maps ({whose}): {own.Count} bindings ({RewiredBindings.PerCategory(own)}); {differences} differ from the game's defaults" +
                $"; {lacking} on elements a standard pad lacks left out. They go to the host, whose virtual pad for you is bound from them.");
            ModLog.Info($"Your pad bindings the game's defaults don't have: {string.Join("; ", InputBindingSet.NotIn(own, defaults, 10).Select(RewiredBindings.Describe))}.");
            return set;
        }
        catch (Exception ex)
        {
            ModLog.Error("Reading your pad's controls failed; the host uses the game's defaults for you", ex);
            return InputBindingSet.PadDefaults(generation);
        }
    }

    /// <summary>
    /// Adds a joystick map's bindings in template terms; returns how many were on elements a standard pad lacks, naming
    /// each in <paramref name="lackingNames"/> when given.
    /// </summary>
    private static int Add(List<InputBinding> bindings, int category, Joystick joystick, ControllerMap? map, List<string>? lackingNames)
    {
        int lacking = 0;
        foreach (ActionElementMap element in Il2CppLists.Items(map?.AllMaps))
        {
            if (element.actionId < 0)
            {
                continue;
            }

            if (RewiredBindings.FromJoystick(category, joystick, element) is { } binding)
            {
                bindings.Add(binding);
            }
            else
            {
                lacking++;
                lackingNames?.Add($"{RewiredBindings.CategoryName(category)}: {RewiredBindings.ActionName(element.actionId)} on {ElementName(joystick, element)}");
            }
        }

        return lacking;
    }

    /// <summary>The game's gamepad template bindings in every category: what a host binds a friend's virtual pad from when the friend is on the defaults.</summary>
    public static List<InputBinding> TemplateDefaults() => RewiredBindings.PadDefaults(RewiredBindings.CategoryIds(), out _);

    /// <summary>The pad's element for the log: its gamepad template name when it has one, else the pad's own element name.</summary>
    private static string ElementName(Joystick joystick, ActionElementMap element)
    {
        ControllerElementIdentifier? template = ReInput.mapping.GetFirstJoystickTemplateElementIdentifier(joystick, element.elementIdentifierId);
        return template != null ? GamepadTemplateLayout.ElementName(template.id) : $"\"{element.elementIdentifierName}\" (no gamepad template element)";
    }
}

/// <summary>A pad's controls as its game has them (<see cref="PadRemapReader.ReadControls"/>).</summary>
/// <param name="Own">Its bindings in gamepad-template terms, saved remaps included.</param>
/// <param name="Defaults">The game's default bindings for that pad, in the same terms.</param>
/// <param name="Differences">How many of <paramref name="Own"/> differ from <paramref name="Defaults"/> (0: the game's defaults).</param>
/// <param name="Lacking">Bindings on elements a standard pad lacks (left out of <paramref name="Own"/>).</param>
/// <param name="LackingNames">Each of those, for the log ("UI: UI Back on Guide (Home, PS or Steam button)").</param>
/// <param name="Whose">Whose maps were read, for the log.</param>
internal readonly record struct PadControls(List<InputBinding> Own, List<InputBinding> Defaults, int Differences, int Lacking, List<string> LackingNames, string Whose);
#endif
