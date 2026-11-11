using System;

namespace T2TOnline.Core.OnlineSettings;

/// <summary>What <see cref="SettingsOpenRequest.Take"/> found.</summary>
/// <param name="Taken">A request was pending and in time: this open is on the Online tab.</param>
/// <param name="Lapsed">A request was pending but older than <see cref="SettingsOpenRequest.Lifetime"/>: dropped, and this open is the game's own.</param>
/// <param name="From">Where the request came from, for the log ("the Online group's row"); empty when there was none.</param>
/// <param name="Age">How old the request was when it was taken or dropped.</param>
public readonly record struct SettingsOpenTake(bool Taken, bool Lapsed, string From, TimeSpan Age);

/// <summary>
/// "Open the game's Settings on its Online tab": asked by a row that then calls the game's own Settings open, taken by the next Settings
/// menu's data step. Taken once; a request older than <see cref="Lifetime"/> lapses (the open it was for never came), so it can't leak into
/// a later plain open. A plain open is otherwise the game's own: the menu resets its opening tab from its data on every open.
/// </summary>
public sealed class SettingsOpenRequest
{
    /// <summary>How long a request waits for its open.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    private TimeSpan _askedAt;
    private string _from = string.Empty;
    private bool _pending;

    /// <summary>True while a request waits (it may have lapsed already: <see cref="Take"/> says).</summary>
    public bool Pending => _pending;

    /// <summary>Asks for the next open to be on the Online tab, at <paramref name="now"/>; a request already waiting is replaced.</summary>
    public void Ask(TimeSpan now, string from)
    {
        _askedAt = now;
        _from = from ?? string.Empty;
        _pending = true;
    }

    /// <summary>Forgets a waiting request.</summary>
    public void Clear() => _pending = false;

    /// <summary>A Settings menu is opening at <paramref name="now"/>: the waiting request, taken or lapsed, and gone either way.</summary>
    public SettingsOpenTake Take(TimeSpan now)
    {
        if (!_pending)
        {
            return default;
        }

        _pending = false;
        TimeSpan age = now - _askedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age <= Lifetime ? new SettingsOpenTake(true, false, _from, age) : new SettingsOpenTake(false, true, _from, age);
    }

    /// <summary>
    /// The tab the menu opens on: the Online tab's index when a request was taken and the tab is in (<paramref name="onlineIndex"/> 0 or more), else
    /// what the game set (<paramref name="gameIndex"/>, from the open's own data).
    /// </summary>
    public static int OpeningIndex(int gameIndex, int onlineIndex, bool taken) => taken && onlineIndex >= 0 ? onlineIndex : gameIndex;
}
