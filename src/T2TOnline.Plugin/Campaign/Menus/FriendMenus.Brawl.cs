#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the brawl offer and its fight controls (<c>BrawlFightMenu</c>, <see cref="BrawlViews"/>): opened with
/// the game's own <c>ShowMenuAsync</c> while this PC shows the host's match, bound at its <c>Show</c> (it takes no data), held
/// up by this PC's offer state while the host's offer is open, switched to the fight controls with the game's own
/// <c>ShowControls</c> when the host's brawl starts, and closed with the host's. It's never text: when it can't be shown
/// for real the log says why and the match stays on screen as before.
/// </summary>
internal sealed partial class FriendMenus
{
    // The frame after binding at which the host's brawl state is applied (the menu's own Show sets its panel first).
    private const int BrawlApplyFrame = 2;

    // The host's brawl menus (by serial) opened again here once for their fight controls (see BrawlUpdated).
    private readonly HashSet<uint> _brawlReopened = new();

    /// <summary>
    /// The brawl menu's request: a fresh object (it has no data of its own; bound at its <c>Show</c>), with this PC's offer held
    /// open for the host's seconds left, so the menu's own <c>Show</c> keeps it up. Null with why when it can't open here.
    /// </summary>
    private Il2CppSystem.Object? PrepareBrawl(View view, MenuData data, out string problem)
    {
        problem = string.Empty;
        int left = BrawlViews.SecondsLeft(data);
        bool fighting = BrawlViews.Fighting(data);
        try
        {
            BrawlViews.HoldOffer(left);
        }
        catch (Exception ex)
        {
            problem = $"this PC's brawl offer state couldn't be set ({ex.GetType().Name})";
            return null;
        }

        Log($"Menus: {view.LogId} opens here as on {LogNames.HostTag()}'s screen: {(fighting ? "the fight controls (the brawl started)" : $"the brawl offer, {left} s left")}.");
        return new Il2CppSystem.Object();
    }

    /// <summary>The host's brawl menu changed (its offer's time, or its brawl started): this PC's offer and panel follow.</summary>
    private void BrawlUpdated(View view, MirroredMenu menu)
    {
        if (view.Mode == Mode.Panel && BrawlViews.Fighting(menu.State.Data) && _brawlReopened.Add(view.Serial))
        {
            // Not up here (its own offer timer closed it first, or this PC wasn't showing the match yet), but the host's brawl
            // started on the same menu: opened again once, with the fight controls.
            Log($"Menus: {LogNames.HostTag()}'s brawl started on {view.LogId}, which isn't up here; opening it again for the fight controls.");
            TryReal(view, menu);
            return;
        }

        if (view.Mode is not (Mode.Real or Mode.Pending))
        {
            return;
        }

        int left = BrawlViews.SecondsLeft(menu.State.Data);
        if (left > 0)
        {
            Try(view, "holding the brawl offer open", () => BrawlViews.HoldOffer(left));
        }

        if (view.Mode == Mode.Real && view.Frame >= BrawlApplyFrame)
        {
            ApplyBrawl(view, menu);
        }
    }

    /// <summary>A brawl view's frames: the host's state once the menu's own <c>Show</c> has set its panel.</summary>
    private void BrawlTick(View view, MirroredMenu menu)
    {
        if (view.Frame == BrawlApplyFrame)
        {
            ApplyBrawl(view, menu);
        }
    }

    /// <summary>The fight controls, with the game's own <c>ShowControls</c>, once the host's brawl started.</summary>
    private void ApplyBrawl(View view, MirroredMenu menu)
    {
        if (!BrawlViews.Fighting(menu.State.Data) || view.Menu?.TryCast<BrawlFightMenu>() is not { } brawl)
        {
            return;
        }

        bool shown = false;
        if (Try(view, "showing the fight controls", () => shown = BrawlViews.ShowControls(brawl)) && shown)
        {
            Log($"Menus: {view.LogId}: the fight controls show here ({LogNames.HostTag()}'s brawl started).");
        }
    }

    /// <summary>
    /// This PC's brawl menu closed by itself while the host's is open (its own offer timer ran out first, <c>TimeUpValidation</c>):
    /// nothing shows in its place; the host's close follows.
    /// </summary>
    private void BrawlClosedHere(View view)
    {
        view.Menu = null;
        view.Mode = Mode.Panel;
        view.Why = "your game closed it";
        Log($"Menus: this PC's game closed {view.LogId} before {LogNames.HostTag()}'s did (its offer timer ended first); nothing is shown in its place.");
    }

    /// <summary>A brawl view is gone (the host closed it, or following stopped): this PC's offer goes back to "no offer".</summary>
    private void BrawlGone()
    {
        _brawlReopened.Clear();
        try
        {
            _ = BrawlViews.ReleaseOffer();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: this PC's brawl offer state couldn't be put back ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
