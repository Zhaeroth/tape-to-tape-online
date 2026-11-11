#if HAS_INTEROP
using System;
using Il2CppSkaters;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// A skater as every log line names it (D127), the one place that writes it: by its asset id as the match began
/// (<see cref="GameMenuCapture.MatchAssetId"/>: the game's own id, or a custom skater's generated one) in Core's shape
/// (<see cref="ObjectEntryLogText.Skater"/>), never by its object name, which the game makes from the skater's names
/// (typed by its player for a custom skater). The id is the same on the host and on a friend, so their lines can be
/// matched. <see cref="LogNames.ObjectName(UnityEngine.GameObject?)"/> writes a skater's object through it.
/// </summary>
internal static class SkaterLogText
{
    /// <summary>
    /// <c>skater 1f0c...</c> for <paramref name="forward"/>, or <c>a skater</c> when it has no usable id; its names are kept
    /// for <see cref="LogNames.ReferenceText"/>. Never throws.
    /// </summary>
    public static string Of(Forward forward)
    {
        try
        {
            LogNames.NoteSkaterNames(forward.name, forward._forwardData?.name);
            return ObjectEntryLogText.Skater(GameMenuCapture.MatchAssetId(forward));
        }
        catch (Exception)
        {
            return ObjectEntryLogText.UnnamedSkater;
        }
    }
}
#endif
