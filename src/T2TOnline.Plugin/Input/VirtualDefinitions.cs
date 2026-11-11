#if HAS_INTEROP
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using Il2CppRewired.Data;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Input;

/// <summary>A custom controller the mod added to Rewired's live data: its id, its elements' ids, the map categories.</summary>
/// <param name="SourceId">The definition's id (Rewired's "source controller id").</param>
/// <param name="AxisElementIds">Rewired element identifier id of each axis, in order.</param>
/// <param name="ButtonElementIds">Rewired element identifier id of each button, in order.</param>
/// <param name="MapCategoryIds">Every map category id in the game's Rewired data.</param>
internal sealed record VirtualDefinition(int SourceId, int[] AxisElementIds, int[] ButtonElementIds, int[] MapCategoryIds);

/// <summary>
/// Adds the mod's custom controller definitions ("T2TOnline Pad", "T2TOnline Keyboard") to Rewired's live configuration,
/// each with an empty default map in every map category.
/// </summary>
/// <remarks>
/// The game defines no custom controller. Rewired reads a custom controller's definition and default maps from this
/// data each time one is created or a map is loaded, so adding them after Rewired has started works, and there's no
/// race with Rewired's own start-up. The maps' contents come from the game's or the friend's bindings later.
/// </remarks>
internal static class VirtualDefinitions
{
    private const int DefaultLayoutId = 0;

    /// <summary>
    /// Adds the definition <paramref name="name"/> (once; an existing one is reused) with <paramref name="axes"/> axes and
    /// <paramref name="buttons"/> buttons. Returns null, with the reason, if Rewired's data can't be reached or the
    /// definition doesn't have those elements. <paramref name="kind"/> ("Virtual pad", "Virtual keyboard") starts the log lines.
    /// </summary>
    public static VirtualDefinition? Ensure(string name, string typeGuid, int axes, int buttons, string kind, out string problem)
    {
        problem = string.Empty;
        InputManager_Base? manager = UnityEngine.Object.FindAnyObjectByType(Il2CppType.Of<InputManager_Base>())?.TryCast<InputManager_Base>();
        UserData? userData = manager?.userData;
        if (userData == null)
        {
            problem = "Rewired's InputManager or its data was not found";
            return null;
        }

        CustomController_Editor? definition = userData.GetCustomController(name);
        if (definition == null)
        {
            definition = new CustomController_Editor();
            definition._id = userData.GetCustomControllerIds().DefaultIfEmpty(-1).Max() + 1;
            definition._name = name;
            definition._descriptiveName = name;
            definition._typeGuidString = typeGuid;
            for (int i = 0; i < axes; i++)
            {
                definition.AddAxis();
            }

            for (int i = 0; i < buttons; i++)
            {
                definition.AddButton();
            }

            userData.customControllers.Add(definition);
        }

        int[] axisIds = definition.GetAxisElementIdentifierIds().ToArray();
        int[] buttonIds = definition.GetButtonElementIdentifierIds().ToArray();
        if (axisIds.Length != axes || buttonIds.Length != buttons)
        {
            problem = $"the definition has {axisIds.Length} axes and {buttonIds.Length} buttons";
            return null;
        }

        int[] categories = userData.GetMapCategoryIds().ToArray();
        // Rewired creates the default maps; its return value isn't reliable (false although the map exists), so check.
        int missingMaps = 0;
        foreach (int category in categories)
        {
            if (userData.GetCustomControllerMap(category, definition.id, DefaultLayoutId) == null)
            {
                userData.CreateCustomControllerMap(category, definition.id, DefaultLayoutId);
                missingMaps += userData.GetCustomControllerMap(category, definition.id, DefaultLayoutId) == null ? 1 : 0;
            }
        }

        if (missingMaps > 0)
        {
            ModLog.Info($"{kind}: {missingMaps} of {categories.Length} default maps could not be added to Rewired's data; Rewired creates empty ones when they're loaded.");
        }

        return new VirtualDefinition(definition.id, axisIds, buttonIds, categories);
    }
}
#endif
