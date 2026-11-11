#if HAS_INTEROP
using System;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Adds the "T2TOnline Pad" custom controller to Rewired's live configuration: 6 axes and 14 buttons in
/// <see cref="PadAxis"/> and <see cref="PadButtons"/> order, plus an empty default map in every map category
/// (<see cref="VirtualDefinitions"/>). The maps' contents come from the game's gamepad bindings, or the friend's own,
/// later (<see cref="VirtualPad"/>).
/// </summary>
internal static class VirtualPadDefinition
{
    /// <summary>The definition's name in Rewired.</summary>
    public const string Name = "T2TOnline Pad";

    private const string TypeGuid = "6f0c3a52-9b8e-4d1f-a7c2-5e4b3d2a1f90";

    /// <summary>The definition's id (Rewired's "source controller id"), or -1 before <see cref="Ensure"/> succeeds.</summary>
    public static int SourceId { get; private set; } = -1;

    /// <summary>Rewired element identifier id of each axis, indexed by <see cref="PadAxis"/>.</summary>
    public static int[] AxisElementIds { get; private set; } = Array.Empty<int>();

    /// <summary>Rewired element identifier id of each button, indexed by bit position in <see cref="PadButtons"/>.</summary>
    public static int[] ButtonElementIds { get; private set; } = Array.Empty<int>();

    /// <summary>Every map category id in the game's Rewired data.</summary>
    public static int[] MapCategoryIds { get; private set; } = Array.Empty<int>();

    /// <summary>Adds the definition once. Returns false, with the reason, if Rewired's data can't be reached.</summary>
    public static bool Ensure(out string problem)
    {
        problem = string.Empty;
        if (SourceId >= 0)
        {
            return true;
        }

        VirtualDefinition? definition = VirtualDefinitions.Ensure(Name, TypeGuid, PadState.AxisCount, PadState.ButtonCount, "Virtual pad", out problem);
        if (definition == null)
        {
            return false;
        }

        AxisElementIds = definition.AxisElementIds;
        ButtonElementIds = definition.ButtonElementIds;
        MapCategoryIds = definition.MapCategoryIds;
        SourceId = definition.SourceId;
        ModLog.Info($"Virtual pad: \"{Name}\" defined in Rewired (id {SourceId}, axes [{string.Join(",", AxisElementIds)}], buttons [{string.Join(",", ButtonElementIds)}], {MapCategoryIds.Length} map categories).");
        return true;
    }
}
#endif
