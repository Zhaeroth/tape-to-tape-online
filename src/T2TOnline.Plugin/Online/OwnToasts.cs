#if HAS_INTEROP
using System;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Marks the toasts the mod itself queues (a notice: "Sam dropped", "Your co-op run couldn't be saved") while they are being queued, so
/// <see cref="Campaign.Menus.Toasts"/> doesn't take them for the game's run toasts: a host would copy them to every friend, and a following friend would hold them back as
/// their own. The router wraps every call into the native screens in <see cref="Begin"/>; the game's <c>NotificationQueue.Queue</c> runs inside that call, on the
/// main thread, so a counter is all it takes. Used as <c>using (OwnToasts.Begin()) { ... }</c>.
/// </summary>
internal static class OwnToasts
{
    private static int _depth;

    /// <summary>True while a toast of the mod's own is being queued.</summary>
    public static bool Active => _depth > 0;

    /// <summary>Starts the mark; dispose it (a <c>using</c>) when the call that queues the toast returns.</summary>
    public static Scope Begin()
    {
        _depth++;
        return default;
    }

    /// <summary>The mark's end.</summary>
    public readonly struct Scope : IDisposable
    {
        /// <summary>Ends the mark. Safe to call more than needed: the count never goes below zero.</summary>
        public void Dispose()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }
}
#endif
