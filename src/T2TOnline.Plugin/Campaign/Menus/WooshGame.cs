#if HAS_INTEROP
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The game's three "Woosh" menu transitions: <c>EMenuTransition.WooshProgress</c>,
/// <c>WooshLoading</c> and <c>WooshRegress</c>, each a menu definition (its <c>Config.DefinitionUniqueID</c>) shown with its
/// <c>Config.TransitionData</c> through <c>UIModule.ShowMenuAsync</c>, as the game's own exits and loaders show them
/// (<c>OldSquadMenu.&lt;Hide&gt;d__110</c>). Game thread only; the callers guard against exceptions.
/// </summary>
internal static class WooshGame
{
    private static string[]? _ids;

    /// <summary>The game's static for <paramref name="kind"/>, or null for <see cref="MenuTransition.None"/>.</summary>
    public static EMenuTransition? Static(MenuTransition kind) => kind switch
    {
        MenuTransition.Progress => EMenuTransition.WooshProgress,
        MenuTransition.Loading => EMenuTransition.WooshLoading,
        MenuTransition.Regress => EMenuTransition.WooshRegress,
        _ => null,
    };

    /// <summary>True for a menu that is one of the game's transitions (its class is a <c>WooshTransition</c>).</summary>
    public static bool IsWoosh(AbstractMenu menu) => menu.TryCast<WooshTransition>() != null;

    /// <summary>
    /// Which transition <paramref name="menu"/> is: its menu definition's name against the three configs' ids (read once),
    /// else, for a <c>WooshLoadingTransition</c>, the loading one; <see cref="MenuTransition.None"/> for any other menu or
    /// one it can't tell.
    /// </summary>
    public static MenuTransition Kind(AbstractMenu menu)
    {
        if (!IsWoosh(menu))
        {
            return MenuTransition.None;
        }

        string id = MenuGame.MenuId(menu);
        _ids ??= new[] { Id(MenuTransition.Progress), Id(MenuTransition.Loading), Id(MenuTransition.Regress) };
        for (int i = 0; i < _ids.Length; i++)
        {
            if (id.Length > 0 && id == _ids[i])
            {
                return (MenuTransition)(i + 1);
            }
        }

        return menu.TryCast<WooshLoadingTransition>() != null ? MenuTransition.Loading : MenuTransition.None;
    }

    /// <summary>The menu definition id of <paramref name="kind"/>'s transition, or "".</summary>
    public static string Id(MenuTransition kind) => Static(kind)?.Config?.DefinitionUniqueID ?? string.Empty;

    /// <summary>
    /// Shows <paramref name="kind"/>'s transition with the game's own call (<c>UIModule.ShowMenuAsync</c>, the generic
    /// called with <c>WooshTransition</c>, the class every transition is; never patched). False, with why, when it can't.
    /// </summary>
    public static bool Show(MenuTransition kind, out string problem)
    {
        MenuTransitionConfig? config = Static(kind)?.Config;
        UIModule? ui = MenuGame.Module();
        if (config == null || string.IsNullOrEmpty(config.DefinitionUniqueID))
        {
            problem = $"this game has no {MenuExits.Name(kind)} transition";
            return false;
        }

        if (ui == null)
        {
            problem = "the game's UI isn't ready";
            return false;
        }

        _ = ui.ShowMenuAsync<WooshTransition>(config.DefinitionUniqueID, config.TransitionData);
        problem = string.Empty;
        return true;
    }
}
#endif
