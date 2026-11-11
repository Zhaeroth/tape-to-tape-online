#if HAS_INTEROP
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppRewired.UI.ControlMapper;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Plugin.Campaign.Menus;
using UnityEngine;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// This PC's own Settings menu, shown for a friend while they're joined
/// (Online/OnlineSession.Remap.cs runs the steps): the call the pause menu's Settings button makes
/// (<c>UIModule.ShowMenuAsync&lt;SettingsMenu&gt;</c> with <c>MenuDefinitionCatalogReference.Settings</c>; the generic is called,
/// never patched), its Controls tab's own remapper (<c>InputSettingsUI.OpenRemapper</c>, what its Remap button runs: Rewired's
/// ControlMapper over the menu), and the menu's own back (<c>SettingsMenu.HandleBack</c>) to close it. It also answers what the
/// friend's UI lock lets through while it's up: the menu's own buttons and back, never its System tab (deleting saves or
/// resetting unlocks while the host's run is followed here) but for its Language row, the friend's own game's language (the
/// game's own dropdown, which answers at the title screen only: <c>MainMenuOnlyDropdown</c>).
/// One per menu shown; nothing here throws past its caller.
/// </summary>
internal sealed class OwnSettingsMenu
{
    /// <summary>The Controls tab's place in the menu's tabs: Video, Gameplay, Play Now, Audio, Controls, System, Stats (verified in the
    /// game's code: its prefab's <c>m_SubMenusInOrder</c>).</summary>
    private const int ControlsTab = 4;

    /// <summary>The menu's id in the game's catalogue (what <c>ShowMenuAsync</c> takes), read once.</summary>
    private static string? _menuId;

    private readonly Il2CppSystem.Collections.Generic.List<AbstractMenu> _menus = new();
    private Il2CppSystem.Collections.Generic.IList<AbstractMenu>? _menusView;
    private SettingsMenu? _menu;
    private InputSettingsUI? _inputs;
    private Transform? _system;
    private Transform? _languageRow;

    /// <summary>True when the menu, once up, has a System tab (its buttons are refused while following).</summary>
    public bool HasSystemTab => _system != null;

    /// <summary>True when the menu, once up, has the System tab's Language row (its clicks are the friend's own).</summary>
    public bool HasLanguageRow => _languageRow != null && !_languageRow.WasCollected;

    /// <summary>The Settings menu once it's up (<see cref="Find"/>), until it's gone; null otherwise.</summary>
    public SettingsMenu? Menu => _menu == null || _menu.WasCollected ? null : _menu;

    /// <summary>True once the menu was found up and it has since gone (destroyed, hidden or hiding).</summary>
    public bool Gone => _menu != null && (_menu.WasCollected || _menu.IsHidden || _menu.IsHiding);

    /// <summary>The menu's Controls tab (its remapper's owner), once the menu is up; null before, or if the menu has none.</summary>
    public InputSettingsUI? Inputs => _inputs == null || _inputs.WasCollected ? null : _inputs;

    /// <summary>
    /// The remapper, while it's open over the menu (opened by <see cref="OpenRemapper"/> or the friend's own press of Remap);
    /// null otherwise.
    /// </summary>
    public ControlMapper? OpenMapper
    {
        get
        {
            ControlMapper? mapper = Inputs?.m_Remapper;
            return mapper != null && !mapper.WasCollected && mapper.isOpen ? mapper : null;
        }
    }

    /// <summary>
    /// The game's own open of its Settings menu, as the pause menu's Settings button asks for it; on its Controls tab when
    /// <paramref name="controlsTab"/> (the remapper's). False, with why, when it can't be asked for.
    /// </summary>
    public static bool Show(bool controlsTab, out string problem)
    {
        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            problem = "the game's UI isn't ready";
            return false;
        }

        _menuId ??= MenuDefinitionCatalogReference.Settings.Name ?? string.Empty;
        if (_menuId.Length == 0)
        {
            problem = "the game's catalogue has no Settings menu";
            return false;
        }

        // Its data as the pause menu leaves it: the first tab (InitData reads OpeningIndex; no data reads as 0 too). Its Show goes to
        // that tab with its own GoToTab and SelectSubMenu (verified in the game's code).
        _ = ui.ShowMenuAsync<SettingsMenu>(_menuId, new SettingsMenu.Metadata { OpeningIndex = controlsTab ? ControlsTab : 0 });
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// The Settings menu once it's up (shown, not hiding), with its Controls tab; null while it isn't yet. Looked for among the
    /// game's open menus: call it while waiting (it reads the menu list each time).
    /// </summary>
    public SettingsMenu? Find()
    {
        if (Menu != null)
        {
            return _menu;
        }

        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            return null;
        }

        _menusView ??= _menus.Cast<Il2CppSystem.Collections.Generic.IList<AbstractMenu>>();
        _menus.Clear();
        ui.GetAllMenus(_menusView);
        for (int i = 0; i < _menus.Count; i++)
        {
            AbstractMenu? menu = _menus[i];
            if (menu != null && !menu.IsHidden && !menu.IsHiding && menu.TryCast<SettingsMenu>() is { } settings)
            {
                _menu = settings;
                _inputs = settings.GetComponentInChildren(Il2CppType.Of<InputSettingsUI>(), true)?.TryCast<InputSettingsUI>();
                _system = settings.GetComponentInChildren(Il2CppType.Of<SystemSettingsUI>(), true)?.transform;
                _languageRow = settings.GetComponentInChildren(Il2CppType.Of<LanguageSettingsUI>(), true)?.TryCast<LanguageSettingsUI>()?.m_LanguageDropdown?.transform.parent;
                break;
            }
        }

        _menus.Clear();
        return Menu;
    }

    /// <summary>Opens the Controls tab's remapper, as its Remap button does. False, with why, when the menu has no Controls tab.</summary>
    public bool OpenRemapper(out string problem)
    {
        InputSettingsUI? inputs = Inputs;
        if (inputs == null)
        {
            problem = "your game's Settings menu has no Controls tab here";
            return false;
        }

        _ = inputs.OpenRemapper();
        problem = string.Empty;
        return true;
    }

    /// <summary>Closes the menu with its own back (the back sound and close the friend's Back would give), unless it's going already.</summary>
    public void Close()
    {
        SettingsMenu? menu = Menu;
        if (menu != null && !menu.IsHidden && !menu.IsHiding)
        {
            menu.HandleBack(false);
        }
    }

    /// <summary>
    /// True when <paramref name="target"/> (a button, toggle or shortcut being clicked) is the menu's own, and not on its
    /// System tab (but for its Language row), and the menu takes input now (no menu over it): what the friend's UI lock lets
    /// through while it's up. With its System tab not found, nothing is: its buttons delete saves and reset unlocks, so a menu
    /// where they can't be told apart answers only its back.
    /// </summary>
    public bool Owns(Component? target)
    {
        // A question its Online tab asked (Leave Online Game, Direct Connections): the game's yes/no over it is the menu's own too.
        if (NativeLobby.Widgets.LobbyPrompts.Owns(target))
        {
            return Menu != null && !Gone;
        }

        SettingsMenu? menu = Menu;
        if (target == null || menu == null || Gone || _system == null || _system.WasCollected)
        {
            return false;
        }

        Transform where = target.transform;
        if (!where.IsChildOf(menu.transform))
        {
            return false;
        }

        // The Language row (the dropdown's navigable and its list) is the friend's own game's language: a click there changes
        // nothing but what this PC's game shows, and its click sound and highlight are the game's.
        if (HasLanguageRow && where.IsChildOf(_languageRow))
        {
            return TakesInput();
        }

        return !where.IsChildOf(_system) && TakesInput();
    }

    /// <summary>
    /// True when the friend's cursor stays on this menu rather than go to <paramref name="target"/>: the menu takes input now and
    /// that's something else (the host's cursor mirrored on a screen under it, which would take a pad's next move there).
    /// </summary>
    public bool KeepsCursorFrom(Component target)
    {
        SettingsMenu? menu = Menu;
        return menu != null && !Gone && !target.transform.IsChildOf(menu.transform) && !NativeLobby.Widgets.LobbyPrompts.Owns(target) && TakesInput();
    }

    /// <summary>True while the menu is up, takes input (no menu over it) and its remapper is closed: a Back closes it then.</summary>
    public bool TakesBack() => Menu != null && !Gone && OpenMapper == null && (TakesInput() || NativeLobby.Widgets.LobbyPrompts.TakesInput());

    /// <summary>True when the game's UI lets the menu take input now (it's the top input handler, or nothing above it blocks).</summary>
    private bool TakesInput()
    {
        UIModule? ui = MenuGame.Module();
        return ui != null && _menu != null && ui.CanProcessInput(_menu);
    }
}
#endif
