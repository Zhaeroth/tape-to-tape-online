#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Dialogue;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The event NPC's speech bubble (v1). On the host's screen the NPC speaks a line from its dialogue pool inside the event
/// screen's intro, after it has come in and before the cards do (picked at random, then marked seen on the host's profile); a
/// friend's game plays the same line itself, in its own language, and marks nothing.
/// <list type="bullet">
/// <item>Host: every bubble line in the game passes the speech controller's <c>ResolveDefaults</c> (its <c>Show</c> resolves
/// each line of a sequence there as it starts); a line of the NPC of an event menu whose intro is playing (the menu's own
/// dialogue, by its speech controller) is found in that NPC's dialogue pool (first visit, a visit line, no choices, a pool
/// entry; else among every speech sequence, by object) and goes to friends as the menu's <see cref="MenuAttachmentKind.NpcLine"/>
/// attachment (<see cref="NpcLineData"/>): once per sequence, at its first line, which also starts the NPC's speaker in the
/// speech mirror (<see cref="SpeechMirror"/>: each line and press of the host's goes to friends). This runs whenever the
/// campaign is shared, friends in or not, so a friend who joins mid-line gets the line and how far it is (both are made when
/// the menu goes on the board). <c>Show</c> itself isn't patched: its cancellation token and anchor side are 8-byte structs
/// the game passes by value, which the interop's detour would read as pointers.</item>
/// <item>Friend: the line is played on this PC's event screen with the NPC's own <c>NpcIntroDialogue.PlaySequence</c> (the
/// same speech controller call, with the NPC's own anchor; it marks nothing seen and saves nothing), once that screen's
/// intro is at its NPC's lines (its own <c>GetCurrentDialogue</c>, which answers none here) or over, and while the host's
/// intro still plays (<see cref="NpcLinePlayback"/>); each of its lines then follows the host's presses (the speech mirror),
/// from the host's line when the host is already past the first (a copy of the sequence from there plays, so no earlier
/// line flashes by); the host's intro ending ends it (with the mod's own cancellation token), as does the screen's close. A
/// line this PC can't find or play is left out, with why.</item>
/// </list>
/// Nothing here throws: the callers guard every entry.
/// </summary>
internal static class EventSpeech
{
    // ---- Host: the lines sent for each event menu the host shows (by the menu). ----
    private static readonly Dictionary<IntPtr, Spoken> HostLines = new();

    // ---- Friend: the host's newest line for each event view (by serial), and when each of this PC's event menus last got
    // to its NPC's lines (its GetCurrentDialogue, refused here). ----
    private static readonly Dictionary<uint, Heard> FriendLines = new();
    private static readonly Dictionary<IntPtr, double> DialoguePoints = new();
    private static bool _bypass;
    private static int _hostProblems;

    /// <summary>The NPC's speaker slot in its event menu (<see cref="SpeechMirror"/>).</summary>
    public const int NpcSlot = 0;

    /// <summary>True while the mod itself asks an event menu for its NPC (the friend's refusal lets it through).</summary>
    public static bool Bypassing => _bypass;

    /// <summary>
    /// Host: the game resolves a bubble line (<c>SpeechBubbleController.ResolveDefaults</c>, each line of every bubble in this
    /// game). A line of an event NPC whose menu's intro plays goes to friends (whenever the campaign is shared: a friend who
    /// joins gets it with the menu); anything else returns at once.
    /// </summary>
    public static void LineResolved(MenuViews views, SpeechBubbleController controller, SpeechLineData line)
    {
        if (!views.Hosting || RunSync.Instance?.HostOnCoopSlot != true)
        {
            return;
        }

        List<(EventChoiceMenu Menu, uint Serial)> menus = views.Host.EventMenus();
        if (menus.Count == 0)
        {
            return;
        }

        foreach ((EventChoiceMenu menu, uint serial) in menus)
        {
            if (!menu.m_IsPlayingIntro)
            {
                continue;
            }

            NpcIntroDialogue? dialogue = menu.GetCurrentDialogue();
            SpeechBubbleController? bubbles = dialogue?._speechBubbles;
            if (dialogue == null || bubbles == null || bubbles.Pointer != controller.Pointer)
            {
                continue;
            }

            Speak(views, menus, menu, serial, dialogue, line);
            return;
        }
    }

    /// <summary>Host: hosting stopped; the lines sent are forgotten.</summary>
    public static void ClearHost() => HostLines.Clear();

    /// <summary>
    /// Host: a line of <paramref name="menu"/>'s NPC started. The first line of a sequence goes to friends (numbered per
    /// menu); the sequence's next lines, and a line resolved twice (a stacking line also passes the game's single-line show),
    /// don't.
    /// </summary>
    private static void Speak(MenuViews views, List<(EventChoiceMenu Menu, uint Serial)> open, EventChoiceMenu menu, uint serial, NpcIntroDialogue dialogue, SpeechLineData line)
    {
        // The menus no longer shown take their lines with them.
        var stale = new List<IntPtr>();
        foreach (IntPtr known in HostLines.Keys)
        {
            if (!open.Exists(m => m.Menu.Pointer == known))
            {
                stale.Add(known);
            }
        }

        stale.ForEach(known => HostLines.Remove(known));
        if (!HostLines.TryGetValue(menu.Pointer, out Spoken? spoken))
        {
            HostLines[menu.Pointer] = spoken = new Spoken();
        }

        IntPtr raw = line.Pointer;
        if (raw == spoken.LastLine || spoken.Lines.IndexOf(raw) > 0)
        {
            spoken.LastLine = raw;
            return;
        }

        spoken.LastLine = raw;
        NpcLineData? data = Identify(dialogue, line, spoken.Count + 1, out SpeechSequence? sequence, out string problem);
        if (data == null || sequence == null)
        {
            if (problem.Length > 0 && ++_hostProblems <= 5)
            {
                ModLog.Warn($"Menus: the host's {MenuGame.MenuId(menu)} (#{serial}) NPC line can't go to friends ({problem}); they go without it.");
            }

            return;
        }

        spoken.Count = data.Number;
        spoken.Lines.Clear();
        Il2CppList.List<SpeechLineData>? lines = sequence.Lines;
        for (int i = 0; lines != null && i < lines.Count; i++)
        {
            spoken.Lines.Add(lines[i]?.Pointer ?? IntPtr.Zero);
        }

        if (!views.Host.Attach(menu, MenuAttachmentKind.NpcLine, s => MenuAttachment.NpcLine(s, data), data.ToString(), views.HasFriends, out problem))
        {
            ModLog.Warn($"Menus: the host's {MenuGame.MenuId(menu)} (#{serial}) NPC {data} can't be sent ({problem}); friends go without it.");
            return;
        }

        // Its lines and the host's presses on them go to friends too (this line is resolved before its bubble shows it).
        if (dialogue._speechBubbles is { } bubbles && dialogue._anchor is { } anchor)
        {
            SpeechMirror.HostBegin(menu, NpcSlot, bubbles, anchor, $"NPC \"{SafeText.Clean(sequence.name, 48)}\"");
        }
    }

    /// <summary>
    /// The host's line as <see cref="NpcLineData"/>: its sequence found in the NPC's dialogue pool by object (first visit, a
    /// visit line, no choices, a pool entry), else among every speech sequence loaded (a training milestone's line). Null,
    /// with why, for a line that isn't a sequence's first, one found nowhere, a campaign tutorial's (another speaker's), or
    /// a name that can't go; "" when it's simply not one to send.
    /// </summary>
    private static NpcLineData? Identify(NpcIntroDialogue dialogue, SpeechLineData line, int number, out SpeechSequence? sequence, out string problem)
    {
        problem = string.Empty;
        sequence = null;
        if (number > ushort.MaxValue)
        {
            return null;
        }

        NpcDialoguePool? pool = dialogue._pool;
        NpcLineSource source = NpcLineSource.None;
        int index = 0;
        int at = -1;
        if (pool != null)
        {
            if ((at = LineIndex(pool.FirstVisitDialogue, line)) >= 0)
            {
                (source, sequence) = (NpcLineSource.FirstVisit, pool.FirstVisitDialogue);
            }
            else if ((at = LineIndex(pool.NoChoicesDialogue, line)) >= 0)
            {
                (source, sequence) = (NpcLineSource.NoChoices, pool.NoChoicesDialogue);
            }

            Il2CppList.List<SpeechSequence>? visits = pool.VisitDialogues;
            for (int i = 0; sequence == null && visits != null && i < visits.Count; i++)
            {
                if ((at = LineIndex(visits[i], line)) >= 0)
                {
                    (source, index, sequence) = (NpcLineSource.Visit, i, visits[i]);
                }
            }

            Il2CppList.List<NpcDialoguePool.Entry>? entries = pool.Dialogues;
            for (int i = 0; sequence == null && entries != null && i < entries.Count; i++)
            {
                SpeechSequence? entry = entries[i]?.Dialogue;
                if ((at = LineIndex(entry, line)) >= 0)
                {
                    (source, index, sequence) = (NpcLineSource.PoolEntry, i, entry);
                }
            }
        }

        if (sequence == null)
        {
            if (EventViews.TutorialActive())
            {
                // The campaign tutorial's own speaker talks during a Coach's intro too (another anchor, the same controller).
                return null;
            }

            foreach (UnityEngine.Object any in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpeechSequence>()))
            {
                SpeechSequence? candidate = any?.TryCast<SpeechSequence>();
                if (candidate != null && (at = LineIndex(candidate, line)) >= 0)
                {
                    (source, sequence) = (NpcLineSource.Other, candidate);
                    break;
                }
            }
        }

        if (sequence == null)
        {
            problem = "its line is in none of this game's speech sequences";
            return null;
        }

        if (at > 0)
        {
            // A sequence already under way (a friend came in mid-line): its next lines aren't sent on their own.
            sequence = null;
            return null;
        }

        string? poolName = pool == null || string.IsNullOrEmpty(pool.name) ? null : pool.name;
        var data = new NpcLineData
        {
            Number = number,
            Source = source,
            Index = index,
            Pool = poolName,
            Sequence = sequence.name ?? string.Empty,
            AnchorSide = (int)dialogue._anchorSide,
        };
        problem = data.Problem() ?? string.Empty;
        return problem.Length == 0 ? data : null;
    }

    /// <summary>The place of <paramref name="line"/> (by object) in <paramref name="sequence"/>'s lines, or -1.</summary>
    private static int LineIndex(SpeechSequence? sequence, SpeechLineData line)
    {
        Il2CppList.List<SpeechLineData>? lines = sequence == null ? null : sequence.Lines;
        for (int i = 0; lines != null && i < lines.Count; i++)
        {
            SpeechLineData? each = lines[i];
            if (each != null && each.Pointer == line.Pointer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Friend: this PC's event menu got to its NPC's lines (its <c>GetCurrentDialogue</c>, from its intro): noted with the time.</summary>
    public static void ReachedDialogue(EventChoiceMenu menu) => DialoguePoints[menu.Pointer] = FrameClock.Seconds;

    /// <summary>Friend: the host's NPC line for event view <paramref name="serial"/> came (a newer one replaces an older one).</summary>
    public static void Arrived(uint serial, NpcLineData line)
    {
        if (!FriendLines.TryGetValue(serial, out Heard? heard))
        {
            FriendLines[serial] = heard = new Heard();
        }

        if (heard.Line == null || line.Number > heard.Line.Number)
        {
            heard.Line = line;
            heard.ArrivedAt = FrameClock.Seconds;
        }
    }

    /// <summary>
    /// Friend, each frame of a real event view (<paramref name="serial"/>, menu <paramref name="open"/>, bound at
    /// <paramref name="boundAt"/>): plays, ends or leaves out the host's newest line (<see cref="NpcLinePlayback"/>).
    /// <paramref name="introOver"/>: this PC's intro is over; <paramref name="hostIntro"/>: the host's intro state (-1 unknown);
    /// <paramref name="hostSpeech"/>: how far the host's NPC is (its speaker's progress), for a late start.
    /// </summary>
    public static void Tick(uint serial, string id, EventChoiceMenu open, double boundAt, bool introOver, int hostIntro, SpeechProgress hostSpeech)
    {
        if (!FriendLines.TryGetValue(serial, out Heard? heard) || heard.Line is not { } line)
        {
            return;
        }

        // Its wait counts from when this screen is up: a friend who joins gets the line with the menu, while its game may still
        // load the run and the map for many seconds, and the host's NPC may still be speaking when it's in.
        bool ready = introOver || (DialoguePoints.TryGetValue(open.Pointer, out double reached) && reached >= boundAt);
        NpcLineStep step = heard.Playback.Step(line.Number, Math.Max(heard.ArrivedAt, boundAt), ready, hostIntro, FrameClock.Seconds);
        switch (step.Action)
        {
            case NpcLineAction.Play:
                string how;
                bool played;
                try
                {
                    played = Play(heard, serial, open, line, hostSpeech, out how);
                }
                catch (Exception ex)
                {
                    played = false;
                    how = $"{ModLog.SafeErrorText(ex)}";
                }

                if (!played && heard.Finished)
                {
                    heard.Playback.Stopped();
                    ModLog.Info($"Menus: {id}: {LogNames.HostTag()}'s NPC {line} came after {LogNames.HostTag()}'s NPC had finished it there ({how}); left out.");
                    return;
                }

                if (!played)
                {
                    heard.Playback.Stopped();
                    ModLog.Warn($"Menus: {id}: {LogNames.HostTag()}'s NPC {line} couldn't be played here ({how}); left out.");
                    return;
                }

                ModLog.Info($"Menus: {id}: {LogNames.HostTag()}'s NPC {line} plays here ({how}, in this PC's language).");
                break;
            case NpcLineAction.Cut:
                Cancel(heard);
                ModLog.Info($"Menus: {id}: {LogNames.HostTag()}'s intro ended; this PC's NPC line {step.Number} ends with it (if it's still speaking).");
                break;
            case NpcLineAction.DropIntroOver:
                ModLog.Info($"Menus: {id}: {LogNames.HostTag()}'s NPC {line} came after {LogNames.HostTag()}'s intro had ended there; left out.");
                break;
            case NpcLineAction.DropTooLate:
                ModLog.Warn($"Menus: {id}: {LogNames.HostTag()}'s NPC {line} waited {NpcLinePlayback.WaitSeconds:0} s for this PC's event screen to get to its NPC's lines; left out.");
                break;
        }
    }

    /// <summary>Friend: event view <paramref name="serial"/>'s menu closed here (it may open again): a line playing ends; what was played stays played.</summary>
    public static void Stop(uint serial)
    {
        if (FriendLines.TryGetValue(serial, out Heard? heard))
        {
            Cancel(heard);
            heard.Playback.Stopped();
        }
    }

    /// <summary>Friend: event view <paramref name="serial"/> is gone: its line ends and is forgotten.</summary>
    public static void Closed(uint serial)
    {
        if (FriendLines.Remove(serial, out Heard? heard))
        {
            Cancel(heard);
        }
    }

    /// <summary>Friend: following started or stopped: every line ends and is forgotten.</summary>
    public static void ClearFriend()
    {
        foreach (Heard heard in FriendLines.Values)
        {
            Cancel(heard);
        }

        FriendLines.Clear();
        DialoguePoints.Clear();
    }

    /// <summary>
    /// Plays <paramref name="line"/> on <paramref name="open"/>'s NPC: its own dialogue (asked past the refusal), the sequence
    /// found in its pool (checked by name) or by name among every speech sequence loaded, then the NPC's own
    /// <c>PlaySequence</c> with a cancellation token of the mod's (a line still playing ends first), its speaker following
    /// the host's (<see cref="SpeechMirror"/>). When the host's NPC is past the first line (<paramref name="hostSpeech"/>), a
    /// copy of the sequence from the host's line plays instead; one past its last line plays nothing
    /// (<see cref="Heard.Finished"/>). False, with why, when anything is missing.
    /// </summary>
    private static bool Play(Heard heard, uint serial, EventChoiceMenu open, NpcLineData line, SpeechProgress hostSpeech, out string how)
    {
        heard.Finished = false;
        NpcIntroDialogue? dialogue;
        _bypass = true;
        try
        {
            dialogue = open.GetCurrentDialogue();
        }
        finally
        {
            _bypass = false;
        }

        if (dialogue == null)
        {
            how = "this PC's event screen has no NPC dialogue for it";
            return false;
        }

        if (dialogue._speechBubbles == null || dialogue._anchor == null)
        {
            how = "this PC's NPC has no speech bubble";
            return false;
        }

        SpeechSequence? sequence = Find(dialogue, line, out how);
        if (sequence == null)
        {
            return false;
        }

        if (line.AnchorSide is int side && side != (int)dialogue._anchorSide)
        {
            how += $"; its bubble's side here is {(int)dialogue._anchorSide}, {side} on the host's";
        }

        // A late start: the host's NPC is past its first line (a line it advanced past isn't shown here).
        int lines = sequence.Lines?.Count ?? 0;
        int from = hostSpeech.IsNone ? 0 : hostSpeech.Stage == SpeechStage.Advanced ? hostSpeech.Key : hostSpeech.Key - 1;
        heard.Finished = from > 0 && from >= lines;
        if (heard.Finished)
        {
            how = $"it's at {hostSpeech} there; this PC's \"{SafeText.Clean(sequence.name, 48)}\" has {lines} line(s)";
            return false;
        }

        SpeechSequence? play = from > 0 ? CopyFrom(sequence, from) : sequence;
        if (play == null)
        {
            how = $"a copy of it from line {from + 1} couldn't be made here";
            return false;
        }

        Cancel(heard);
        if (from > 0)
        {
            heard.Copy = play;
            how += $"; it starts here at line {from + 1} ({hostSpeech} on the host's screen)";
        }

        // The speaker follows the host's from its first line here (begun before the sequence shows that line).
        SpeechMirror.FriendBegin(serial, NpcSlot, dialogue._speechBubbles, dialogue._anchor, $"NPC \"{SafeText.Clean(sequence.name, 48)}\"", from + 1);
        var cancel = new Il2CppSystem.Threading.CancellationTokenSource();
        heard.Cancellation = cancel;
        _ = dialogue.PlaySequence(play, cancel.Token);
        return true;
    }

    /// <summary>
    /// A copy of <paramref name="sequence"/> from line <paramref name="from"/> on (the same line objects, delay, side and
    /// localization prefix; the same name), made with the non-generic <c>ScriptableObject.CreateInstance</c> and never saved;
    /// null when it can't be made. Destroyed when its line ends (<see cref="Cancel"/>).
    /// </summary>
    private static SpeechSequence? CopyFrom(SpeechSequence sequence, int from)
    {
        SpeechSequence? copy = ScriptableObject.CreateInstance(Il2CppType.Of<SpeechSequence>())?.TryCast<SpeechSequence>();
        Il2CppList.List<SpeechLineData>? lines = sequence.Lines;
        if (copy == null || lines == null)
        {
            return null;
        }

        var kept = new Il2CppList.List<SpeechLineData>();
        for (int i = from; i < lines.Count; i++)
        {
            kept.Add(lines[i]);
        }

        copy.hideFlags = HideFlags.DontSave;
        copy.name = sequence.name;
        copy.Lines = kept;
        copy.DelayBetween = sequence.DelayBetween;
        copy.AnchorSide = sequence.AnchorSide;
        copy.LocalizationPrefix = sequence.LocalizationPrefix;
        return copy;
    }

    /// <summary>
    /// The host's sequence on this PC: in the NPC's own pool by where it was (its pool's and the sequence's names checked);
    /// else the one speech sequence loaded with its name. Null, with why, when there's none or more than one.
    /// </summary>
    private static SpeechSequence? Find(NpcIntroDialogue dialogue, NpcLineData line, out string how)
    {
        NpcDialoguePool? pool = dialogue._pool;
        if (pool != null && line.Source != NpcLineSource.Other && pool.name == line.Pool)
        {
            SpeechSequence? inPool = line.Source switch
            {
                NpcLineSource.FirstVisit => pool.FirstVisitDialogue,
                NpcLineSource.NoChoices => pool.NoChoicesDialogue,
                NpcLineSource.Visit => pool.VisitDialogues is { } visits && line.Index < visits.Count ? visits[line.Index] : null,
                NpcLineSource.PoolEntry => pool.Dialogues is { } entries && line.Index < entries.Count ? entries[line.Index]?.Dialogue : null,
                _ => null,
            };
            if (inPool != null && inPool.name == line.Sequence)
            {
                how = "from this NPC's own pool";
                return inPool;
            }
        }

        SpeechSequence? found = null;
        int count = 0;
        foreach (UnityEngine.Object any in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpeechSequence>()))
        {
            SpeechSequence? candidate = any?.TryCast<SpeechSequence>();
            if (candidate != null && candidate.name == line.Sequence)
            {
                found ??= candidate;
                count++;
            }
        }

        how = count == 1 ? "found by its name"
            : count == 0 ? $"no speech sequence \"{HostText.KeyForLog(line.Sequence)}\" is in this game"
            : $"{count} speech sequences are called \"{HostText.KeyForLog(line.Sequence)}\" here";
        return count == 1 ? found : null;
    }

    /// <summary>
    /// Ends the line playing here, if any (the mod's own token: the bubble goes as when the game cancels its own), and
    /// destroys a late start's copy of the sequence.
    /// </summary>
    private static void Cancel(Heard heard)
    {
        Il2CppSystem.Threading.CancellationTokenSource? cancel = heard.Cancellation;
        SpeechSequence? copy = heard.Copy;
        heard.Cancellation = null;
        heard.Copy = null;
        try
        {
            if (cancel != null)
            {
                cancel.Cancel();
                cancel.Dispose();
            }

            if (copy != null)
            {
                UnityEngine.Object.Destroy(copy);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: ending an NPC line on this PC failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Host: the lines sent for one event menu.</summary>
    private sealed class Spoken
    {
        /// <summary>The last line number sent.</summary>
        public int Count { get; set; }

        /// <summary>The line resolved last (a stacking line is resolved twice in a row).</summary>
        public IntPtr LastLine { get; set; }

        /// <summary>The lines of the sequence sent last, in order.</summary>
        public List<IntPtr> Lines { get; } = new();
    }

    /// <summary>Friend: the host's newest line for one event view, and how it plays here.</summary>
    private sealed class Heard
    {
        public NpcLinePlayback Playback { get; } = new();

        public NpcLineData? Line { get; set; }

        public double ArrivedAt { get; set; }

        /// <summary>The line playing here's cancellation, or null.</summary>
        public Il2CppSystem.Threading.CancellationTokenSource? Cancellation { get; set; }

        /// <summary>A late start's copy of the sequence (from the host's line), destroyed when its line ends; or null.</summary>
        public SpeechSequence? Copy { get; set; }

        /// <summary>The last try found the host's NPC already past this PC's last line: nothing to play.</summary>
        public bool Finished { get; set; }
    }
}
#endif
