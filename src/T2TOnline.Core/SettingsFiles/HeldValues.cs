using System;
using System.Collections.Generic;
using System.Threading;

namespace T2TOnline.Core.SettingsFiles;

/// <summary>What a change of a setting does now (<see cref="TourSettingHold.Decide"/>).</summary>
public enum TourSettingAction
{
    /// <summary>No check tour runs: the setting changes and its file is saved, as ever.</summary>
    Write,

    /// <summary>A check tour runs: the setting changes in memory only (<see cref="HeldValues"/>); its file isn't touched.</summary>
    HoldInMemory,

    /// <summary>A check tour runs and the setting is one a tour may never change, even in memory: refused.</summary>
    Refuse,
}

/// <summary>
/// The rule for a setting changed while a check tour runs: held in memory, never written, so a tour on a player's own PC
/// leaves their settings file as it was (MelonLoader saves every entry's value at quit, so the entry itself isn't changed
/// either); and the one setting that would share the player's IP address (<see cref="DirectConnections"/>) can't change at all.
/// </summary>
public static class TourSettingHold
{
    /// <summary>The setting that lets Steam connect players directly, sharing their IP addresses: never changed by a tour.</summary>
    public const string DirectConnections = "AllowDirectConnections";

    /// <summary>What changing setting <paramref name="name"/> does now.</summary>
    public static TourSettingAction Decide(bool tourRunning, string name) =>
        !tourRunning ? TourSettingAction.Write
        : string.Equals(name, DirectConnections, StringComparison.OrdinalIgnoreCase) ? TourSettingAction.Refuse
        : TourSettingAction.HoldInMemory;
}

/// <summary>
/// Settings' values held in memory over what their entries hold (a check tour's changes, <see cref="TourSettingHold"/>):
/// read first by every setting's getter. Copy on write: a read never takes a lock and sees a whole map (none at all,
/// the usual case, is one field read); <see cref="Hold"/> and <see cref="Drop"/> come from the game's thread.
/// </summary>
public sealed class HeldValues
{
    private readonly object _gate = new();
    private Dictionary<string, object>? _values;

    /// <summary>True while any value is held.</summary>
    public bool Any => Volatile.Read(ref _values) != null;

    /// <summary>Holds <paramref name="value"/> for <paramref name="name"/> (replacing one held before).</summary>
    public void Hold(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            Dictionary<string, object>? before = Volatile.Read(ref _values);
            var next = before == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(before, StringComparer.Ordinal);
            next[name] = value;
            Volatile.Write(ref _values, next);
        }
    }

    /// <summary>The value held for <paramref name="name"/>, when one of type <typeparamref name="T"/> is.</summary>
    public bool TryGet<T>(string name, out T value)
    {
        Dictionary<string, object>? values = Volatile.Read(ref _values);
        if (values != null && values.TryGetValue(name, out object? held) && held is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Lets every held value go (the entries' own values apply again) and returns their names, sorted.</summary>
    public IReadOnlyList<string> Drop()
    {
        lock (_gate)
        {
            Dictionary<string, object>? before = Volatile.Read(ref _values);
            Volatile.Write(ref _values, null);
            if (before == null)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(before.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
