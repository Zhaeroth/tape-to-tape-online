#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The speech mirror, part: a late friend's Canada curse dialogue. The menu's <c>PlayDialogue</c>
/// (<c>d__44</c>) builds each line in code and can't start at the host's line, as an event NPC's copy of its sequence
/// does, and its methods take a <c>CancellationToken</c> by value, so none can be patched. It runs from line 1 here, and the
/// mirror presses each line the host is past, one press a frame (<see cref="SpeechFollow"/>). While it does, the speech canvas's
/// <c>enabled</c> is off (a bubble holds no canvas of its own; verified in the game's code: <c>Bubble_Normal</c>'s prefab; they all draw through
/// <c>SpeechBubbleController.m_SpeechCanvas</c>) and the bubbles' three sounds are refused (<see cref="BubblesQuiet"/>), so
/// nothing flashes by and nothing is heard. It comes back on at the host's line, when this PC's cards are up, at its close, when
/// following stops, or after <see cref="LateSpeechCover.MaxHiddenSeconds"/> whatever happens; its scene's unload takes it along.
/// A failure leaves the old look: the lines flash by, logged.
/// </summary>
internal static partial class SpeechMirror
{
    /// <summary>What every sound of a speech bubble's path starts with (verified in the game's code: <c>SpeechBubbleView</c>'s appear, change and
    /// letter paths).</summary>
    public const string BubbleSounds = "event:/UI/_General/ui_general_textbubble_";

    private static readonly List<LateSpeech> LateSpeeches = new();

    /// <summary>True while a late speech runs hidden here: its bubbles' sounds are refused (the only reader: a static read per one-shot).</summary>
    public static bool BubblesQuiet { get; private set; }

    /// <summary>The Canada curse menu of the host's <paramref name="serial"/> is bound here: watched until its first line starts.</summary>
    private static void LateWatch(uint serial, CanadaCurseMenu menu, SpeechBubbleController bubbles)
    {
        LateClosed(serial);
        LateSpeeches.Add(new LateSpeech(serial, menu, bubbles));
    }

    /// <summary>Every frame: a watched speech hides once the host is past its first line before this PC's has started, and shows again.</summary>
    private static void LateTick(MenuMirror mirror, double now)
    {
        for (int i = LateSpeeches.Count - 1; i >= 0; i--)
        {
            LateSpeech late = LateSpeeches[i];
            try
            {
                if (LateStep(late, mirror, now))
                {
                    LateSpeeches.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                LateSpeeches.RemoveAt(i);
                LateShow(late);
                ModLog.Warn($"Menus: {late.Id}: following a late start of {LogNames.HostTag()}'s dialogue failed ({ModLog.SafeErrorText(ex)}); its bubbles show.");
            }
        }

        LateQuiet();
    }

    /// <summary>One frame of a watched speech. True when it's over.</summary>
    private static bool LateStep(LateSpeech late, MenuMirror mirror, double now)
    {
        if (Mirrored(mirror, late.Serial) is not { } menu || late.Bubbles == null || late.Menu == null)
        {
            LateShow(late);
            return true;
        }

        late.Id = menu.State.Id;
        bool lines = false;
        bool arrived = false;
        foreach (FriendSpeaker speaker in Friends)
        {
            if (speaker.Serial == late.Serial)
            {
                lines |= speaker.Follow.Lines > 0;
                arrived |= LateSpeechCover.OnHostsLine(speaker.Follow.OwnKey, speaker.Follow.Phase, HostProgress(menu, speaker.Slot));
            }
        }

        bool past = LateSpeechCover.PastFirstLine(HostProgress(menu, PucklinSlot), HostProgress(menu, HatfreezeSlot));
        switch (late.Cover.Next(past, lines, arrived, CanadaViews.CardsUp(late.Menu), now))
        {
            case LateSpeechStep.Hide:
                Canvas? canvas = late.Bubbles.m_SpeechCanvas;
                if (canvas != null && canvas.enabled)
                {
                    canvas.enabled = false;
                    late.Hidden = canvas;
                    ModLog.Info($"Menus: {late.Id}: {LogNames.HostTag()}'s dialogue is past its first line; this PC's runs hidden up to {LogNames.HostTag()}'s line (at most {LateSpeechCover.MaxHiddenSeconds:0} s), so none flashes by.");
                }

                return false;
            case LateSpeechStep.Show:
                LateShow(late);
                double ms = late.Cover.HiddenFor * 1000;
                switch (late.Cover.Ended)
                {
                    case LateSpeechEnd.Arrived:
                        ModLog.Info($"Menus: {late.Id}: this PC's dialogue reached {LogNames.HostTag()}'s line; its bubbles show from here (hidden {ms:0} ms).");
                        break;
                    case LateSpeechEnd.Finished:
                        ModLog.Info($"Menus: {late.Id}: {LogNames.HostTag()}'s dialogue was over; this PC's ran out hidden and its cards are up (hidden {ms:0} ms).");
                        break;
                    default:
                        ModLog.Warn($"Menus: {late.Id}: this PC's dialogue didn't reach {LogNames.HostTag()}'s line within {LateSpeechCover.MaxHiddenSeconds:0} s; its bubbles show from here.");
                        break;
                }

                return true;
            default:
                return late.Cover.Over;
        }
    }

    /// <summary>The host's menu closed here: a speech running hidden shows again.</summary>
    private static void LateClosed(uint serial)
    {
        for (int i = LateSpeeches.Count - 1; i >= 0; i--)
        {
            if (LateSpeeches[i].Serial == serial)
            {
                LateShow(LateSpeeches[i]);
                LateSpeeches.RemoveAt(i);
            }
        }

        LateQuiet();
    }

    /// <summary>Following stopped: every speech running hidden shows again (<paramref name="gameClosing"/>: no game calls).</summary>
    private static void LateClear(bool gameClosing)
    {
        if (!gameClosing)
        {
            foreach (LateSpeech late in LateSpeeches)
            {
                LateShow(late);
            }
        }

        LateSpeeches.Clear();
        BubblesQuiet = false;
    }

    /// <summary>The speech canvas back on, if it's still here. Never throws.</summary>
    private static void LateShow(LateSpeech late)
    {
        Canvas? canvas = late.Hidden;
        late.Hidden = null;
        try
        {
            if (canvas != null && !canvas.WasCollected)
            {
                canvas.enabled = true;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: this PC's speech bubbles couldn't be shown again ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static void LateQuiet()
    {
        bool quiet = false;
        foreach (LateSpeech late in LateSpeeches)
        {
            quiet |= late.Hidden != null;
        }

        BubblesQuiet = quiet;
    }

    /// <summary>A Canada curse menu's dialogue, watched from its bind until its first line or until it's on the host's.</summary>
    private sealed class LateSpeech
    {
        public LateSpeech(uint serial, CanadaCurseMenu menu, SpeechBubbleController bubbles)
        {
            Serial = serial;
            Menu = menu;
            Bubbles = bubbles;
            Id = $"menu #{serial}";
        }

        public uint Serial { get; }

        public CanadaCurseMenu Menu { get; }

        public SpeechBubbleController Bubbles { get; }

        public string Id { get; set; }

        public LateSpeechCover Cover { get; } = new();

        /// <summary>The speech canvas this switched off, while it's off.</summary>
        public Canvas? Hidden { get; set; }
    }
}
#endif
