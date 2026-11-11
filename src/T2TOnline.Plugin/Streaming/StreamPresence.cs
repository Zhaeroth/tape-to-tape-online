#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSkaters;
using Il2CppUltimateReplay;
using T2TOnline.Core.Snapshots;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using BrokenStick = Il2CppPools.BrokenStick;
using Stick = Il2CppPools.Stick;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Which of the stream's objects are shown on a friend's PC (<see cref="StreamPlayer"/>): an object is shown while the
/// snapshot has it and hidden while it doesn't, as on the host (pools, effects), and put back as it was when playback
/// ends. One local object can stand for several stream ids (the host announced it twice). Two things the host's game does
/// with an object it shows are done here too, as the host's game does them (D71):
/// <list type="bullet">
/// <item>A skater shown by the stream for the first time this playback was inactive since its scene loaded, so its own
/// <c>Forward.Start</c> hasn't run: Unity runs <c>Start</c> before the object's next update, but the player drives it this
/// frame, and its replay update (<c>ReplayForward.AnimationReplayOnOnAfterApplyAnimation</c>, every replay frame) calls
/// <c>Forward.HideFire</c>, which reads the Spine slots only <c>Start</c>'s <c>InitSlotsAndAttachments</c> finds (seen as
/// one NullReferenceException per campaign match, that skater's first frame without its after-apply
/// visuals). So for such a skater the game's own <c>Forward.InitSlotsAndAttachments</c> runs right away (lookups and field
/// writes only; its <c>Start</c> repeats it a frame later).</item>
/// <item>A stick of the game's stick pool, whole or broken, is in the host's stream exactly while its renderers are on: the
/// pool's <c>ActivateStick</c> adds it to the record scene and turns its renderer (a broken stick's two parts) on,
/// <c>DeactivateStick</c> and <c>DeactivateBrokenStick</c> do both back, and an idle pool stick is an active object with its
/// renderers off, never <c>SetActive(false)</c>. A friend's pool turned its own sticks' renderers off at its start, and
/// nothing here turns them on (the activators are game logic), so a thrown or broken stick the stream shows has its
/// renderers turned on while it's shown, as the host's are, and put back off when playback ends.</item>
/// </list>
/// </summary>
internal sealed class StreamPresence
{
    /// <summary>Skater set-ups and stick renderers logged per playback (further ones are counted).</summary>
    private const int LoggedPerPlayback = 3;

    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private readonly List<string> _setUpNames = new();
    private int _setUps;
    private int _setUpFailures;
    private int _sticksDrawn;
    private bool _stickReadout;

    /// <summary>Starts tracking <paramref name="replayObject"/>'s object as stream id <paramref name="netId"/>.</summary>
    public void Add(ushort netId, ReplayObject replayObject)
    {
        GameObject gameObject = replayObject.gameObject;
        if (!_entries.TryGetValue(gameObject.Pointer, out Entry? entry))
        {
            (MeshRenderer? stick, MeshRenderer? stickPart) = StickRenderersOf(gameObject);
            entry = new Entry(gameObject, gameObject.activeSelf, stick, stickPart);
            _entries[gameObject.Pointer] = entry;
        }

        if (!entry.Ids.Contains(netId))
        {
            entry.Ids.Add(netId);
        }
    }

    /// <summary>Stream id <paramref name="netId"/> no longer names this object (it stands for another one now).</summary>
    public void RemoveId(ushort netId, GameObject gameObject)
    {
        if (_entries.TryGetValue(gameObject.Pointer, out Entry? entry))
        {
            entry.Ids.Remove(netId);
        }
    }

    /// <summary>Stops tracking an object (destroyed, or about to be).</summary>
    public void Forget(IntPtr gameObject) => _entries.Remove(gameObject);

    /// <summary>Stops tracking objects destroyed since (Unity's null).</summary>
    public void Prune()
    {
        List<IntPtr>? gone = null;
        foreach ((IntPtr pointer, Entry entry) in _entries)
        {
            if (entry.Object == null)
            {
                (gone ??= new List<IntPtr>()).Add(pointer);
            }
        }

        gone?.ForEach(pointer => _entries.Remove(pointer));
    }

    /// <summary>
    /// Shows what <paramref name="frame"/> has and hides the rest; <paramref name="now"/> stamps what's shown. A skater shown
    /// for the first time gets its Spine slots set up and a pool stick its renderer, before the player drives them this
    /// frame (see the class remarks).
    /// </summary>
    public void Show(SnapshotFrame frame, double now)
    {
        _setUpNames.Clear();
        foreach (Entry entry in _entries.Values)
        {
            bool want = false;
            for (int i = 0; i < entry.Ids.Count && !want; i++)
            {
                want = frame.Objects.ContainsKey(entry.Ids[i]);
            }

            if (want)
            {
                entry.LastShown = now;
            }

            if (want != entry.Shown && entry.Object != null)
            {
                entry.Object.SetActive(want);
                entry.Shown = want;
                entry.Toggles++;
                if (want && !entry.WasActive && !entry.SetUpChecked)
                {
                    entry.SetUpChecked = true;
                    SetUpBeforeStart(entry);
                }
            }

            // A plain reference check: this runs for every stream object every frame, and few are sticks.
            if (entry.StickRenderer is not null)
            {
                DrawStick(entry, want);
            }
        }

        if (_setUpNames.Count > 0 && _setUps - _setUpNames.Count < LoggedPerPlayback)
        {
            ModLog.Info($"Stream player: {string.Join(", ", _setUpNames)} shown by the stream before {(_setUpNames.Count == 1 ? "its" : "their")} first frame here; " +
                "Spine slots set up with the game's own Forward.InitSlotsAndAttachments (as Forward.Start does a frame later)" +
                $"{(_setUps >= LoggedPerPlayback ? "; further ones are counted" : string.Empty)}.");
        }
    }

    /// <summary>Seconds the object has been hidden by the stream (0 while it's shown or unknown).</summary>
    public double HiddenFor(IntPtr gameObject, double now) =>
        _entries.TryGetValue(gameObject, out Entry? entry) && !entry.Shown ? now - entry.LastShown : 0;

    /// <summary>Puts every object's visibility (and a pool stick's renderer) back as it was and logs what came and went.</summary>
    public void Restore()
    {
        int toggled = 0;
        foreach (Entry entry in _entries.Values)
        {
            toggled += entry.Toggles > 0 ? 1 : 0;
            try
            {
                if (entry.Object != null && entry.Object.activeSelf != entry.WasActive)
                {
                    entry.Object.SetActive(entry.WasActive);
                }

                if (entry.StickTurnedOn && entry.StickRenderer != null)
                {
                    entry.StickRenderer.enabled = false;
                }

                if (entry.StickTurnedOn && entry.StickPart != null)
                {
                    entry.StickPart.enabled = false;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Stream player: couldn't put an object's visibility back: {ModLog.SafeErrorText(ex)}");
            }
        }

        if (toggled > 0)
        {
            string names = string.Join(", ", _entries.Values.Where(p => p.Toggles > 0).GroupBy(p => p.Name)
                .OrderByDescending(g => g.Sum(p => p.Toggles)).Take(8).Select(g => $"{g.Key} ×{g.Count()} ({g.Sum(p => p.Toggles)} toggles)"));
            ModLog.Info($"Stream player: {toggled} objects came and went with the stream (shown and hidden {_entries.Values.Sum(p => p.Toggles)} times): {names}.");
        }

        if (_setUps + _setUpFailures + _sticksDrawn > 0)
        {
            ModLog.Info($"Stream player: {_setUps} skater(s) shown before their first frame here had their Spine slots set up with Forward.InitSlotsAndAttachments" +
                $"{(_setUpFailures > 0 ? $" ({_setUpFailures} couldn't be)" : string.Empty)}; {_sticksDrawn} thrown stick(s) the stream showed had their renderer " +
                "turned on here, as the host's game draws a pool stick exactly while it's in the stream (put back off now).");
        }

        Clear();
    }

    /// <summary>Forgets everything without touching the objects (a new playback).</summary>
    public void Clear()
    {
        _entries.Clear();
        _setUpNames.Clear();
        _setUps = _setUpFailures = _sticksDrawn = 0;
        _stickReadout = false;
    }

    /// <summary>
    /// The renderers of a pool stick (the game's <c>Pools.Stick._MeshRenderer</c>, or <c>Pools.BrokenStick</c>'s two parts'),
    /// or nulls for anything else.
    /// </summary>
    private static (MeshRenderer? First, MeshRenderer? Second) StickRenderersOf(GameObject gameObject)
    {
        try
        {
            Stick? stick = gameObject.GetComponent<Stick>();
            if (stick != null)
            {
                return (stick._MeshRenderer is MeshRenderer whole && whole != null ? whole : null, null);
            }

            BrokenStick? broken = gameObject.GetComponent<BrokenStick>();
            if (broken == null)
            {
                return (null, null);
            }

            MeshRenderer? first = broken._MeshRendererPart1 is MeshRenderer part1 && part1 != null ? part1 : null;
            MeshRenderer? second = broken._MeshRendererPart2 is MeshRenderer part2 && part2 != null ? part2 : null;
            return first != null ? (first, second) : (second, null);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Stream player: couldn't read whether \"{LogNames.ObjectName(gameObject)}\" is a pool stick: {ModLog.SafeErrorText(ex)}");
            return (null, null);
        }
    }

    /// <summary>
    /// A skater just shown for the first time this playback: if its own <c>Start</c> hasn't set its Spine slots, the game's
    /// <c>Forward.InitSlotsAndAttachments</c> does now. A failure costs that skater one frame, as before; never throws.
    /// </summary>
    private void SetUpBeforeStart(Entry entry)
    {
        try
        {
            Forward? forward = entry.Object.GetComponent<Forward>();
            if (forward == null || forward._fireSlot != null)
            {
                return;
            }

            forward.InitSlotsAndAttachments();
            _setUps++;
            _setUpNames.Add(entry.Name);
        }
        catch (Exception ex)
        {
            if (_setUpFailures++ < LoggedPerPlayback)
            {
                ModLog.Warn($"Stream player: couldn't set up {entry.Name}'s Spine slots before its first frame here ({ModLog.SafeErrorText(ex)}); its own Start does it a frame later.");
            }
        }
    }

    /// <summary>
    /// A pool stick follows the host's rule (see the class remarks): drawn while the stream has it. Turned on once per
    /// showing (not checked every frame); the first one per playback says what it found.
    /// </summary>
    private void DrawStick(Entry entry, bool shown)
    {
        if (!shown)
        {
            entry.StickChecked = false;
            return;
        }

        if (entry.StickChecked)
        {
            return;
        }

        entry.StickChecked = true;
        try
        {
            MeshRenderer renderer = entry.StickRenderer!;
            bool wasOn = renderer.enabled;
            if (!_stickReadout)
            {
                _stickReadout = true;
                ModLog.Info(wasOn
                    ? $"Stream player: the first thrown stick the stream showed ({entry.Name}) was drawn here already (its renderer on)."
                    : $"Stream player: the first thrown stick the stream showed ({entry.Name}) had its renderer off here (the pool's own idle state); " +
                      "it's drawn while the host's is (the host's game draws a pool stick exactly while it's in the stream).");
            }

            if (!wasOn)
            {
                renderer.enabled = true;
                entry.StickTurnedOn = true;
                _sticksDrawn++;
            }

            if (entry.StickPart != null && !entry.StickPart.enabled)
            {
                entry.StickPart.enabled = true;
                entry.StickTurnedOn = true;
            }
        }
        catch (Exception ex)
        {
            entry.StickRenderer = null;
            ModLog.Warn($"Stream player: couldn't draw the thrown stick \"{entry.Name}\": {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>One local object's visibility while playing: as it was, as shown now, and the stream ids that are it.</summary>
    private sealed class Entry
    {
        public Entry(GameObject gameObject, bool wasActive, MeshRenderer? stickRenderer, MeshRenderer? stickPart)
        {
            Object = gameObject;
            Name = LogNames.ObjectName(gameObject);
            WasActive = wasActive;
            Shown = wasActive;
            LastShown = FrameClock.Seconds;
            StickRenderer = stickRenderer;
            StickPart = stickPart;
        }

        public GameObject Object { get; }

        /// <summary>Its name for a log line (a skater's object goes by its asset id: <see cref="LogNames.ObjectName(GameObject?)"/>).</summary>
        public string Name { get; }

        public bool WasActive { get; }

        public bool Shown { get; set; }

        public double LastShown { get; set; }

        public int Toggles { get; set; }

        public List<ushort> Ids { get; } = new();

        /// <summary>Its first showing this playback was looked at for a skater whose Start hasn't run.</summary>
        public bool SetUpChecked { get; set; }

        /// <summary>A pool stick's renderer (null for anything else, or after it failed).</summary>
        public MeshRenderer? StickRenderer { get; set; }

        /// <summary>A broken pool stick's second part's renderer, or null.</summary>
        public MeshRenderer? StickPart { get; }

        /// <summary>The stick's renderer was looked at since it was last shown.</summary>
        public bool StickChecked { get; set; }

        /// <summary>This playback turned the stick's renderer on (put back off at the end).</summary>
        public bool StickTurnedOn { get; set; }
    }
}
#endif
