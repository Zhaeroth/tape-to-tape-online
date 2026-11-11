#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The two notes that are a normal path, not a failure (D142): the host in their settings (<see cref="LobbyWords.NoteHostInSettings"/>), the host at
/// their new unlocks while this player has none (<see cref="LobbyWords.NoteNoNewUnlocks"/>). Each is the game's own toast here, once per host
/// menu, in this player's language with the host's name (kept from rich text by <see cref="LobbyToast"/>), instead of the text panel. The toast is
/// queued as the mod's own (<see cref="OwnToasts"/>): a following friend's game doesn't hold it back as a run toast, and only a host sends toasts
/// to anyone, so it stays on this PC. Should the game have no toast queue then, or the toast fail, the text panel shows the note as before
/// (logged with why). A failure's note (no word) stays on the panel.
/// </summary>
internal sealed partial class FriendMenus
{
    /// <summary>The same note isn't toasted again within this long (a host opening Settings again and again).</summary>
    private const double NoteToastAgainSeconds = 20;

    // When each note's word was last toasted here (FrameClock seconds), by its key.
    private readonly Dictionary<string, double> _noteToastedAt = new(StringComparer.Ordinal);

    /// <summary>What became of a note's toast.</summary>
    private enum NoteToastState
    {
        /// <summary>Not tried yet.</summary>
        None,

        /// <summary>On screen (or toasted moments ago): the text panel stays clear.</summary>
        Shown,

        /// <summary>It couldn't go: the text panel shows the note.</summary>
        Failed,
    }

    /// <summary>
    /// A note view in <see cref="RefreshPanel"/>: true when the game's toast carries it (tried once per view; the panel then stays clear), false
    /// when the panel must show it (a failure's note, or a toast that couldn't go). Never throws.
    /// </summary>
    private bool NoteAsToast(View view)
    {
        if (view.NoteWord is not { } word || view.NoteToast == NoteToastState.Failed)
        {
            return false;
        }

        if (view.NoteToast == NoteToastState.Shown)
        {
            return true;
        }

        double now = FrameClock.Seconds;
        if (_noteToastedAt.TryGetValue(word.Key, out double at) && now - at < NoteToastAgainSeconds)
        {
            view.NoteToast = NoteToastState.Shown;
            Log($"Menus: {view.LogId}: the same note was the game's toast {now - at:0} s ago; not again.");
            return true;
        }

        string? why;
        try
        {
            using (OwnToasts.Begin())
            {
                why = LobbyToast.Show(new LobbyLine(word, HostName), ToastTiming.Now) ? null : "the game has no toast queue here";
            }
        }
        catch (Exception ex)
        {
            why = $"the game's toast failed: {ModLog.SafeErrorText(ex)}";
        }

        if (why == null)
        {
            view.NoteToast = NoteToastState.Shown;
            _noteToastedAt[word.Key] = now;
            Log($"Menus: {view.LogId}: the note is the game's toast here ({word.Key}), not the text panel.");
            return true;
        }

        view.NoteToast = NoteToastState.Failed;
        ModLog.Info($"Menus: {view.LogId}: the note's toast couldn't be shown ({why}); the text panel shows it.");
        return false;
    }

    /// <summary>A host menu's view: what became of its note's toast.</summary>
    private sealed partial class View
    {
        /// <summary><see cref="Mode.Note"/> with a word: whether the game's toast carries it (<see cref="NoteAsToast"/>).</summary>
        public NoteToastState NoteToast { get; set; }
    }
}
#endif
