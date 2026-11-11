using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's held inputs in a menu, as menu values. The game shows more while a hold
/// shortcut is down ("hold for details", the left trigger on a pad): the keyword boxes under the selected card, a skater's
/// talents beside its card, an event's card details, a lineup's compare box. The host's hold and release go to friends as a
/// value on the shortcut's path (<see cref="MenuEventKind.ValueSet"/>, like a list selector's), <see cref="Held"/> while it's
/// down and <see cref="Released"/> once it's up, so a friend's copy fires the same shortcut's own hold events. The two
/// values sit above every list selector's index (<see cref="MaxListIndex"/>), so a value on a path is one or the other,
/// never both. No wire change: the values fit the existing value event (up to <see cref="MenuTarget.MaxIndex"/>).
/// </summary>
public static class MenuHold
{
    /// <summary>The shortcut is up again (its hold released).</summary>
    public const int Released = 1000;

    /// <summary>The shortcut is held down.</summary>
    public const int Held = 1001;

    /// <summary>The largest list selector index a menu value carries (the host sends none above it).</summary>
    public const int MaxListIndex = Released - 1;

    /// <summary>The value for a hold (<paramref name="held"/>) or a release.</summary>
    public static int Value(bool held) => held ? Held : Released;

    /// <summary>True when <paramref name="value"/> is a list selector's index (not a hold, a release or a shortcut's press).</summary>
    public static bool IsListIndex(int value) => value is >= 0 and <= MaxListIndex;

    /// <summary>True when <paramref name="value"/> is a hold or a release, not a list selector's index.</summary>
    public static bool IsHold(int value) => value is Released or Held;

    /// <summary>Reads a value: true, with <paramref name="held"/>, when it's a hold or a release; false for anything else.</summary>
    public static bool TryRead(int value, out bool held)
    {
        held = value == Held;
        return IsHold(value);
    }
}

/// <summary>One hold to apply on a friend's copy of a menu: the shortcut, and whether it goes down or up.</summary>
/// <param name="Target">The shortcut's path in the menu.</param>
/// <param name="Held">True to hold it, false to release it.</param>
public readonly record struct HoldStep(MenuTarget Target, bool Held);

/// <summary>
/// A friend's side of one menu's held inputs (<see cref="MenuHold"/>): what the host holds (<see cref="Want"/>), what this
/// PC's copy of the menu was given (<see cref="Applied"/>), and so what's due once the copy is ready for it
/// (<see cref="Due"/>). A copy starts with nothing held; when it closes, every shortcut still held here is released first
/// (<see cref="HeldNow"/>, then <see cref="Reset"/>), so a menu the game keeps and shows again doesn't keep the host's old
/// hold. A shortcut this PC's copy doesn't have is skipped until the copy is made again. Pure logic, in the order the host's
/// shortcuts first came; not thread-safe (one game thread).
/// </summary>
public sealed class HoldFollower
{
    private readonly List<MenuTarget> _order = new();
    private readonly Dictionary<MenuTarget, bool> _wanted = new();
    private readonly HashSet<MenuTarget> _applied = new();
    private readonly HashSet<MenuTarget> _skipped = new();

    /// <summary>Holds and releases applied here so far (each change once).</summary>
    public int Followed { get; private set; }

    /// <summary>Holds applied here so far (a shortcut going down).</summary>
    public int Holds { get; private set; }

    /// <summary>Shortcuts the host holds now.</summary>
    public int Wanted => _wanted.Count(w => w.Value);

    /// <summary>The host's <paramref name="target"/> went down (<paramref name="held"/>) or up.</summary>
    public void Want(MenuTarget target, bool held)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_wanted.ContainsKey(target))
        {
            _order.Add(target);
        }

        _wanted[target] = held;
    }

    /// <summary>What to apply now, in the order the shortcuts first came: each one whose state here differs from the host's.</summary>
    public IReadOnlyList<HoldStep> Due()
    {
        var due = new List<HoldStep>();
        foreach (MenuTarget target in _order)
        {
            bool wanted = _wanted[target];
            if (!_skipped.Contains(target) && wanted != _applied.Contains(target))
            {
                due.Add(new HoldStep(target, wanted));
            }
        }

        return due;
    }

    /// <summary><paramref name="target"/> was held (<paramref name="held"/>) or released on this PC's copy.</summary>
    public void Applied(MenuTarget target, bool held)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool changed = held ? _applied.Add(target) : _applied.Remove(target);
        if (changed)
        {
            Followed++;
            Holds += held ? 1 : 0;
        }
    }

    /// <summary>This PC's copy has no such shortcut: it's left alone until the copy is made again (<see cref="Reset"/>).</summary>
    public void Skip(MenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _skipped.Add(target);
    }

    /// <summary>The shortcuts held on this PC's copy now, in the order they first came (to release before it closes).</summary>
    public IReadOnlyList<MenuTarget> HeldNow() => _order.Where(_applied.Contains).ToList();

    /// <summary>This PC's copy closed (or is made again): nothing is held on it, nothing skipped; the host's holds stay wanted.</summary>
    public void Reset()
    {
        _applied.Clear();
        _skipped.Clear();
    }
}
