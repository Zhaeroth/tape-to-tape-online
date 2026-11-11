using System;

namespace T2TOnline.Steam;

/// <summary>
/// Whether Steam's in-game overlay is open, as the game's Facepunch.Steamworks says it (its <c>OnGameOverlayActivated</c>
/// event, which the mod's plugin listens to: this library registers no callbacks, D20). The lobby ignores the game's pad
/// and keyboard while it is open, and a host who opened the invite window is told it closed. Written by the event's
/// handler and read on the main thread; never throws.
/// <para>
/// A close that is never heard must not leave the lobby deaf for good, so <see cref="IsOpen"/> stops believing an overlay
/// that has been "open" for longer than <see cref="MaxOpen"/>; <see cref="Active"/> is the raw last event.
/// </para>
/// </summary>
public sealed class SteamOverlayWatch
{
    /// <summary>How long an overlay is believed to be open without another event (a profile page can be read for a long while, not forever).</summary>
    public static readonly TimeSpan MaxOpen = TimeSpan.FromMinutes(10);

    private readonly Func<long> _tickMs;
    private long _changedAt;
    private bool _closedUntaken;

    /// <summary>A watch on the real clock.</summary>
    public SteamOverlayWatch()
        : this(() => Environment.TickCount64)
    {
    }

    internal SteamOverlayWatch(Func<long> tickMs) => _tickMs = tickMs ?? throw new ArgumentNullException(nameof(tickMs));

    /// <summary>The one the plugin's event handler writes and the lobby reads.</summary>
    public static SteamOverlayWatch Shared { get; } = new();

    /// <summary>True while the last event heard said the overlay opened (false until the first one).</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// True while the overlay is open as far as the events say, and not for more than <see cref="MaxOpen"/> since the event
    /// that opened it. The lobby's input guard asks this.
    /// </summary>
    public bool IsOpen => Active && _tickMs() - _changedAt <= (long)MaxOpen.TotalMilliseconds;

    /// <summary>How many times the overlay has opened since the game started.</summary>
    public int Opened { get; private set; }

    /// <summary>Records an overlay event: <paramref name="active"/> is true as it opens and false as it closes.</summary>
    public void Set(bool active)
    {
        if (active == Active)
        {
            return;
        }

        Active = active;
        _changedAt = _tickMs();
        if (active)
        {
            Opened++;
        }
        else
        {
            _closedUntaken = true;
        }
    }

    /// <summary>True once after the overlay has closed (for "Steam's invite window closed"); false until it closes again.</summary>
    public bool TryTakeClosed()
    {
        bool closed = _closedUntaken;
        _closedUntaken = false;
        return closed;
    }
}
