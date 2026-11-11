#if HAS_INTEROP
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Il2CppBishopGames.Module;
using Il2CppBishopGames.UI;
using Il2CppI2.Loc;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppState;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using UnityEngine.SceneManagement;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>The game's UI, scenes, services and text, as the menu views read them.</summary>
internal static class MenuGame
{
    // TextMeshPro rich-text tags (<color=#fff>, <sprite name="x">, <b>...), which the panel shows as plain text.
    private static readonly Regex Tags = new("<[^<>]{1,96}>", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The game's UI module (menus and the one navigation cursor): <c>BishopModule</c> keeps each module under its own
    /// class and its base classes (<c>BishopModule.Get&lt;UIModule&gt;</c> is how the game finds it), so this looks it up
    /// there without calling the generic.
    /// </summary>
    public static UIModule? Module()
    {
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, AbstractModule>? modules = BishopModule.s_Modules;
        Il2CppSystem.Type key = Il2CppType.Of<UIModule>();
        return modules != null && modules.ContainsKey(key) ? modules[key]?.TryCast<UIModule>() : null;
    }

    /// <summary>The scene the game has active ("start_screen", "world_map", "standard_game"...), or "".</summary>
    public static string SceneName() => SceneManager.GetActiveScene().name ?? string.Empty;

    /// <summary>A menu's id: its definition asset's name (what <c>ShowMenuAsync</c> takes), or "".</summary>
    public static string MenuId(AbstractMenu menu) => menu.MenuDefinition?.name ?? string.Empty;

    /// <summary><paramref name="name"/> when it can travel as a menu id, class or scene name (printable ASCII, short), else <paramref name="fallback"/>.</summary>
    public static string Name(string name, string fallback) =>
        name.Length is > 0 and <= MenuState.MaxNameBytes && name[0] != ' ' && name[^1] != ' ' && name.All(c => c is >= ' ' and <= '~') ? name : fallback;

    /// <summary>The class of a game object's script, without its namespace.</summary>
    public static string TypeName(Il2CppSystem.Object value) => value.GetIl2CppType()?.Name ?? string.Empty;

    /// <summary>The game's campaign state (the run on this PC: the host's run, on a friend's PC that follows).</summary>
    public static CampaignState? Campaign()
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<CampaignState>());
        return found?.TryCast<CampaignState>();
    }

    /// <summary>An interface-typed service from the game's injector, wrapped as the interface.</summary>
    public static T? Repository<T>(Func<IntPtr, T> wrap)
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());
        return found == null ? null : wrap(found.Pointer);
    }

    /// <summary>A text as the player sees it: tags taken out, white space run together, made safe and cut to <paramref name="max"/>.</summary>
    public static string Plain(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string plain = Spaces.Replace(Tags.Replace(text, string.Empty), " ").Trim();
        return plain.Length == 0 ? string.Empty : SafeText.Clean(plain, max);
    }

    /// <summary>A text component's text as the player sees it (see <see cref="Plain"/>), or "" when it's hidden.</summary>
    public static string Shown(TMP_Text? text, int max)
    {
        if (text == null || !text.isActiveAndEnabled || text.color.a < 0.05f)
        {
            return string.Empty;
        }

        return Plain(text.text, max);
    }

    /// <summary>
    /// <paramref name="term"/> in this PC's language, or "" when the game has no translation or it needs parameters the
    /// host filled in (then the host's own text is shown instead).
    /// </summary>
    public static string Localized(string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return string.Empty;
        }

        string? text = LocalizationManager.GetTranslation(term, true, 0, true, false, null, null, true);
        return string.IsNullOrEmpty(text) || text.Contains('{') || text.Contains('[') ? string.Empty : text;
    }
}
#endif
