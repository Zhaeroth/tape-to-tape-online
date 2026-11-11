#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The game's own sprites by name, each with a fallback chain: found in the running game,
/// so nothing is shipped. A sprite is only there while its bundle is loaded, so the index is made again when a looked-for sprite has gone.
/// Each use has a chain ending in nothing: the caller shows text or leaves it out, and says so.
/// </summary>
internal static class NativeArt
{
    /// <summary>The host's mark.</summary>
    public const string Crown = "crown";

    /// <summary>The ready tick.</summary>
    public const string ReadyTick = "ready";

    /// <summary>An empty seat.</summary>
    public const string Plus = "plus";

    /// <summary>A step of the signal bars.</summary>
    public const string Pip = "pip";

    /// <summary>A controller's outline.</summary>
    public const string Controller = "controller";

    /// <summary>A controller's fill, tinted with the player's colour.</summary>
    public const string ControllerFill = "controllerFill";

    /// <summary>A keyboard's outline.</summary>
    public const string Keyboard = "keyboard";

    /// <summary>A keyboard's fill, tinted with the player's colour.</summary>
    public const string KeyboardFill = "keyboardFill";

    /// <summary>The panel kit's fill.</summary>
    public const string Panel = "panel";

    /// <summary>The panel kit's outline.</summary>
    public const string PanelOutline = "panelOutline";

    /// <summary>A section's header.</summary>
    public const string Header = "header";

    /// <summary>A thin divider.</summary>
    public const string Divider = "divider";

    /// <summary>The lightning between splits.</summary>
    public const string Lightning = "lightning";

    /// <summary>The ready banner's arrow.</summary>
    public const string ReadyBanner = "readyBanner";

    /// <summary>The Start frame.</summary>
    public const string StartFrame = "startFrame";

    /// <summary>The Start button, allowed.</summary>
    public const string StartOn = "startOn";

    /// <summary>The Start button, not allowed yet.</summary>
    public const string StartOff = "startOff";

    /// <summary>A warning.</summary>
    public const string Warning = "warning";

    /// <summary>The panel kit's mask: a filled rounded panel.</summary>
    public const string PanelMask = "panelMask";

    /// <summary>The panel kit's highlight outline.</summary>
    public const string PanelEdge = "panelEdge";

    /// <summary>A lock: can't be chosen.</summary>
    public const string Lock = "lock";

    /// <summary>A small tick: an invite out.</summary>
    public const string Tick = "tick";

    /// <summary>Something in progress.</summary>
    public const string Dots = "dots";

    /// <summary>A note: someone invited you.</summary>
    public const string Tooltip = "tooltip";

    /// <summary>A plain square, to tint.</summary>
    public const string Square = "square";

    private const double ReindexSeconds = 2;

    // Each use's sprites, the first one found wins. Only the last is a stand-in.
    private static readonly Dictionary<string, string[]> Chains = new(StringComparer.Ordinal)
    {
        [Crown] = new[] { "crown", "difficulty_crowns_0" }, // the squad screen's: held by OnlineGroup.OptionalSources, as what the game has loaded after a match is the second, a paper crown (observed in the running game)
        [ReadyTick] = new[] { "icon-ready", "icon-checkmark", "Checkmark" },
        [Plus] = new[] { "icon-plus", "icon-artefact-empty" },
        [Pip] = new[] { "pip-square", "square32" },
        [Controller] = new[] { "icon-controller-blank" },
        [ControllerFill] = new[] { "icon-controller-fill-white", "icon-controller-fill-grey" },
        [Keyboard] = new[] { "icon-keyboard-blank" },
        [KeyboardFill] = new[] { "icon-keyboard-fill-white", "icon-keyboard-fill-grey" },
        [Panel] = new[] { "background-tooltip-priority-04", "background-tooltip-priority-03", "square32" },
        [PanelOutline] = new[] { "background-tooltip-priority-04-outline", "square-outline-32" },
        [Header] = new[] { "bg-slice-header", "bg-header-banner-02", "bg-header" },
        [Divider] = new[] { "bg-line-gradient", "bg-match-gradient" },
        [Lightning] = new[] { "T-lightning-02", "T-lightning-01" },
        [ReadyBanner] = new[] { "bg-ready-arrow-01", "bg-ready-arrow-body-01" },
        [StartFrame] = new[] { "bg-start-frame" },
        [StartOn] = new[] { "bg-button-ready", "bg-button-ready-blue" },
        [StartOff] = new[] { "bg-button-ready-off" },
        [Warning] = new[] { "icon-warning", "icon-tooltip" },
        [PanelMask] = new[] { "background-tooltip-priority-04-mask", "background-tooltip-priority-04", "square32" },
        [PanelEdge] = new[] { "background-tooltip-priority-04-outline-highlight", "background-tooltip-priority-04-outline", "square-outline-32" },
        [Lock] = new[] { "icon-lock", "big_lock" }, // the Compendium's: held by OnlineGroup.OptionalSources, or the word alone
        [Tick] = new[] { "icon-checkmark", "Checkmark", "icon-ready" },
        [Dots] = new[] { "Circle Filled - x64", "pip-square" },
        [Tooltip] = new[] { "icon-tooltip", "icon-warning" },
        [Square] = new[] { "square32" },
    };

    private static readonly Dictionary<string, Sprite> Index = new(StringComparer.Ordinal);
    private static double _indexedAt = double.MinValue;
    private static string? _lastReport;

    /// <summary>Changes when a source that only icons need has come: what's drawn may have a better sprite now.</summary>
    public static int Version { get; private set; }

    /// <summary>The roles the kit knows.</summary>
    public static IEnumerable<string> Roles => Chains.Keys;

    /// <summary>The sprite for <paramref name="role"/>, or null: none of its chain is loaded. <paramref name="fellBack"/>: it isn't the chain's first.</summary>
    public static Sprite? Get(string role, out bool fellBack)
    {
        fellBack = false;
        if (!Chains.TryGetValue(role, out string[]? chain))
        {
            return null;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                if (Index.TryGetValue(chain[i], out Sprite? sprite) && sprite != null && !sprite.WasCollected)
                {
                    fellBack = i > 0;
                    return sprite;
                }
            }

            // Nothing known: look again, not more often than the bundles can change.
            if (pass == 0 && !Reindex())
            {
                break;
            }
        }

        return null;
    }

    /// <summary>The sprite for <paramref name="role"/>, or null.</summary>
    public static Sprite? Get(string role) => Get(role, out _);

    /// <summary>
    /// A held source that only icons need has come (<see cref="OnlineGroup.OptionalSources"/>): the next look at a sprite finds what it brought. A chain only looks again with nothing found, so without this a
    /// stand-in stays for good. With a screen up it looks now, as that screen redraws, and the log says if that changed anything; with none, the pass over the sprites waits until one opens.
    /// </summary>
    public static void SourceArrived()
    {
        Index.Clear();
        _indexedAt = double.MinValue;
        Version++;
        if (LobbyShell.IsShown)
        {
            Report();
        }
    }

    /// <summary>A pass over the loaded sprites, and one line in the log: found N of M, what's missing or a fallback. Slow: at a title and in the tour, not per frame.</summary>
    public static string Report()
    {
        _indexedAt = double.MinValue;
        Reindex();
        var missing = new List<string>();
        var fellBack = new List<string>();
        int found = 0;
        foreach (string role in Chains.Keys)
        {
            if (Get(role, out bool fallback) == null)
            {
                missing.Add(role);
                continue;
            }

            found++;
            if (fallback)
            {
                fellBack.Add(role);
            }
        }

        string report = $"Native art: found {found} of {Chains.Count}" +
            (fellBack.Count > 0 ? $"; by a fallback: {string.Join(", ", fellBack)}" : string.Empty) +
            (missing.Count > 0 ? $"; missing: {string.Join(", ", missing)}" : string.Empty) + ".";
        if (report != _lastReport)
        {
            _lastReport = report;
            ModLog.Info(report);
        }

        return report;
    }

    /// <summary>False when it was made too recently to look again.</summary>
    private static bool Reindex()
    {
        double now = FrameClock.Seconds;
        if (now - _indexedAt < ReindexSeconds)
        {
            return false;
        }

        _indexedAt = now;
        Index.Clear();
        var wanted = new HashSet<string>(Chains.Values.SelectMany(c => c), StringComparer.Ordinal);
        try
        {
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
            {
                if (found?.TryCast<Sprite>() is { } sprite && sprite.name is { Length: > 0 } name && wanted.Contains(name) && !Index.ContainsKey(name))
                {
                    Index[name] = sprite;
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Native art: the game's sprites couldn't be read ({ModLog.SafeErrorText(ex)}).");
        }

        return true;
    }
}
#endif
