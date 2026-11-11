#if HAS_INTEROP
using System;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Adds the "T2TOnline Keyboard" custom controller to Rewired's live configuration: 64 buttons (button <c>i</c> is element
/// <c>i</c> of a keyboard friend's key table, <see cref="InputBindingSet.Elements"/>), no axes, plus an empty default map in
/// every map category (<see cref="VirtualDefinitions"/>). Rewired has one keyboard, so a second keyboard player can only be
/// a custom controller; its maps come from the friend's own bindings (<see cref="VirtualKeyboard"/>).
/// </summary>
internal static class VirtualKeyboardDefinition
{
    /// <summary>The definition's name in Rewired.</summary>
    public const string Name = "T2TOnline Keyboard";

    private const string TypeGuid = "b3e1d9a4-5c27-4f86-9e0b-7a6d2c18f453";

    /// <summary>The definition's id (Rewired's "source controller id"), or -1 before <see cref="Ensure"/> succeeds.</summary>
    public static int SourceId { get; private set; } = -1;

    /// <summary>Rewired element identifier id of each button, indexed by key-table element.</summary>
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

        VirtualDefinition? definition = VirtualDefinitions.Ensure(Name, TypeGuid, 0, KeyboardMouseState.MaxElements, "Virtual keyboard", out problem);
        if (definition == null)
        {
            return false;
        }

        ButtonElementIds = definition.ButtonElementIds;
        MapCategoryIds = definition.MapCategoryIds;
        SourceId = definition.SourceId;
        ModLog.Info($"Virtual keyboard: \"{Name}\" defined in Rewired (id {SourceId}, {ButtonElementIds.Length} buttons, {MapCategoryIds.Length} map categories).");
        return true;
    }
}
#endif
