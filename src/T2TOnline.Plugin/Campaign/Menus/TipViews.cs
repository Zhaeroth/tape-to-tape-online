#if HAS_INTEROP
using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Dialogue;
using Il2CppTape2Tape.Speech;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's first-time campaign tips on friends (v1; a couch player sees them). A tip is the blacksmith's speech over a
/// dark overlay (<c>CampaignTips.ShowAsync</c>: <c>CampaignTipsConfig.GetTip</c>, the profile's seen check, three frames, then
/// <c>CampaignDialoguePresenter.PlayBlacksmithSpeech(tip.sequence, true)</c>, verified in the game's code;
/// only after it ends, <c>SeenDialogues.MarkSeen</c> and a profile save).
/// <list type="bullet">
/// <item>Host: the tip lookup (<c>GetTip</c>, already patched, <see cref="Scenes.GameTips"/>) arms a tip the host's profile
/// hasn't seen; it's up once the presenter's dark overlay shows within <see cref="CampaignTipTimeline.ConfirmSeconds"/> (its
/// own fade-in turns it on; verified in the game's code: FadeInOverlay), and over when the overlay goes (its fade-out turns
/// it off; verified in the game's code). While it's up and a friend is in, it's a menu-board item of its own
/// (<see cref="CampaignTipData"/>), so a friend who joins mid-tip gets it too; its close goes with the tip's end.</item>
/// <item>Friend: once this PC is on the item's scene and has a presenter, the same tip (by id, in this PC's own tip list; its
/// sequence's name checked) plays with the presenter's own <c>PlayBlacksmithSpeech</c>, in this PC's language. Nothing is
/// marked seen and nothing is saved: <c>PlayBlacksmithSpeech</c> does neither (only <c>ShowAsync</c> does, after it, on the
/// host), and this PC's own tips keep their disabled stand-in (<see cref="Scenes.GameTips"/>: they'd mark this profile's seen
/// set and wait for a press nobody here can give). When the host's tip ends, this PC's runs out: the game's own per-anchor
/// skip (<c>SpeechBubbleController.Skip</c>, as the mod's own press, <see cref="SpeechMirror.FriendPress"/>) each frame until
/// its overlay has gone (its own end restores its input block and sends the blacksmith off); one still up after
/// <see cref="CampaignTipTimeline.RunOutSeconds"/> is taken down (its bubbles cleared, its overlay hidden, the blacksmith sent
/// off) and logged. A tip that comes while this PC's last one still runs out waits its turn.</item>
/// <item>Its lines: every speech line waits for a press, so the host's presses and line starts follow with the speech mirror
/// (<see cref="SpeechMirror"/>, a context speaker: a tip is in no menu). The speaker on both PCs is the presenter's blacksmith
/// (<c>CampaignDialoguePresenter.blacksmith</c>, a <c>SpeechActor</c>: its <c>anchor</c> on its own <c>bubbleController</c>; verified
/// in the game's code: <c>SpeechActor.PlaySpeech</c>); the host's value goes on the tip's board item
/// (<see cref="HostMenus.ItemValue"/>, so a friend who joins mid-tip gets it and catches up), and a friend's bubble follows it with
/// the game's own skip, one press a frame, whatever its language.</item>
/// </list>
/// A failure here is logged and ends the tip, never the menus around it.
/// </summary>
internal static class TipViews
{
    // The speech mirror's name for a tip's speech, and its one speaker.
    private const string Speech = CampaignTipData.Type;
    private const int Speaker = 0;

    private static readonly PresenterCache HostPresenter = new();
    private static readonly PresenterCache FriendPresenter = new();

    // ---- Host: the tip the game looked up (armed), and the one up. ----
    private static Armed? _armed;
    private static HostTip? _shown;
    private static bool _unsendableLogged;

    // ---- Friend: the host's tip as this PC shows it, and a newer one waiting for that one to run out. ----
    private static FriendTip? _tip;
    private static FriendTip? _next;

    /// <summary>
    /// Host: the game looked up a tip to show (<c>GetTip</c>'s postfix): armed when it will show (enabled, tips on, not seen
    /// on this profile; the game's other rules decide whether its overlay comes). Reads only.
    /// </summary>
    public static void HostAsked(CampaignTipsConfig config, TipDefinition tip)
    {
        if (tip.disabled || !config.tipsEnabled || string.IsNullOrEmpty(tip.id) || Il2Cpp.ProfileData.Instance?.seenDialogues?.IsSeen(tip.id) == true)
        {
            return;
        }

        SpeechSequence? sequence = tip.sequence;
        // As the game names them (a friend finds its own copy by id); one that can't go (over-long) isn't armed, logged once.
        var data = new CampaignTipData(tip.id, sequence?.name ?? string.Empty, Math.Clamp(sequence?.Lines?.Count ?? 0, 0, CampaignTipData.MaxLines),
            Dark: true); // tips always show the overlay (verified in the game's code)
        string? problem = data.Problem();
        _armed = problem == null ? new Armed(data, FrameClock.Seconds) : null;
        if (problem != null && !_unsendableLogged)
        {
            _unsendableLogged = true;
            ModLog.Warn($"Menus: the host's campaign tip \"{SafeText.Clean(tip.id, 48)}\" can't go to friends ({problem}; logged once).");
        }
    }

    /// <summary>
    /// Host, every frame: an armed tip whose overlay shows is up (on the board once a friend is in), with its speech followed
    /// for friends, and one up whose overlay has gone is over (its board item closed). Nothing is read while no tip is armed or
    /// up.
    /// </summary>
    public static void HostTick(HostMenus host, bool friends)
    {
        if (_armed == null && _shown == null)
        {
            return;
        }

        try
        {
            HostStep(host, friends);
        }
        catch (Exception ex)
        {
            // A tip's failure isn't the menus': its board item closes (friends' run out), and they carry on.
            uint serial = _shown?.Serial ?? 0;
            _armed = null;
            _shown = null;
            ModLog.Warn($"Menus: the host's campaign tip couldn't be followed ({ModLog.SafeErrorText(ex)}); friends' ends here.");
            SpeechMirror.HostEnd(Speech);
            host.CloseItem(serial);
        }
    }

    /// <summary>Host: hosting stopped (its board went with it): the tips armed and up are forgotten.</summary>
    public static void ClearHost()
    {
        _armed = null;
        _shown = null;
        HostPresenter.Forget();
    }

    /// <summary>
    /// Friend: a menu event from the host (<paramref name="menu"/>: the mirror's copy, null once closed). True when it's the
    /// host's tip's (taken here; the menus leave it alone); a sync without the tip ends it (and goes on to the menus).
    /// </summary>
    public static bool Mirror(MenuMirrorChange change, MenuEvent menuEvent, MirroredMenu? menu, MenuMirror mirror)
    {
        if (change is MenuMirrorChange.Opened or MenuMirrorChange.Refreshed && menu != null && menu.State.Type == CampaignTipData.Type)
        {
            if (Of(menu.State.Serial) is { } known)
            {
                known.Menu = menu; // sent again (a rejoin)
                return true;
            }

            if (!CampaignTipData.TryRead(menu.State.Data, out CampaignTipData? read))
            {
                ModLog.Warn("Menus: the host's campaign tip came malformed; left out.");
                return true;
            }

            var tip = new FriendTip(menu, read!, FrameClock.Seconds);
            if (_tip == null)
            {
                _tip = tip;
            }
            else
            {
                // One still here runs out first (it holds this PC's blacksmith and overlay); this one's turn comes after it.
                Ended(_tip);
                _next = tip;
            }

            return true;
        }

        if (Of(menuEvent.Serial) is not { } own)
        {
            if (change == MenuMirrorChange.Synced)
            {
                EndedIfGone(_tip, mirror);
                EndedIfGone(_next, mirror);
            }

            return false;
        }

        if (change == MenuMirrorChange.Closed)
        {
            Ended(own);
        }

        return true;
    }

    /// <summary>
    /// Friend, every frame while following: the host's tip plays once this PC is on its scene with a presenter, follows the
    /// host's lines, and runs out once the host's has ended (<see cref="CampaignTipTimeline.Friend"/>).
    /// </summary>
    public static void FriendTick()
    {
        if (_tip == null)
        {
            if (_next == null)
            {
                return;
            }

            _tip = _next;
            _next = null;
        }

        try
        {
            FriendStep(_tip);
        }
        catch (Exception ex)
        {
            // A tip's failure isn't the menus': it's taken down here, and they carry on.
            FriendTip? failed = _tip;
            _tip = null;
            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{failed?.LogId}\" failed here ({ModLog.SafeErrorText(ex)}).");
            if (failed is { Started: true })
            {
                TakeDown(failed, "its step failed");
                SpeechMirror.FriendEnd(Speech);
            }
        }
    }

    /// <summary>Friend: following stopped (or started). A tip still showing here is taken down at once (no frames left to run it out), unless the game is closing.</summary>
    public static void StopFriend(bool gameClosing)
    {
        if (_tip is { Started: true } tip && !gameClosing && FriendPresenter.OverlayUp())
        {
            TakeDown(tip, "following stopped");
        }

        if (!gameClosing)
        {
            SpeechMirror.FriendEnd(Speech);
        }

        _tip = null;
        _next = null;
        FriendPresenter.Forget();
    }

    private static void HostStep(HostMenus host, bool friends)
    {
        double now = FrameClock.Seconds;
        bool overlayUp = HostPresenter.OverlayUp();
        if (_armed != null)
        {
            _armed.Frames++;
        }

        switch (CampaignTipTimeline.Host(_armed?.At, _shown?.Since, overlayUp, now, _armed?.Frames ?? 0))
        {
            case HostTipStep.Disarm:
                ModLog.Info($"Menus: the host's campaign tip \"{SafeText.Clean(_armed!.Tip.Id, 48)}\" didn't come up (the game's own rules kept it back); nothing goes to friends.");
                _armed = null;
                return;
            case HostTipStep.Show:
                _shown = new HostTip(_armed!.Tip, now);
                _armed = null;
                ModLog.Info($"Menus: the host's campaign tip \"{SafeText.Clean(_shown.Tip.Id, 48)}\" is up (sequence \"{_shown.Tip.Sequence}\", {_shown.Tip.Lines} line(s)).");
                BeginHostSpeech(_shown);
                break;
            case HostTipStep.End:
                HostTip ended = _shown!;
                _shown = null;
                SpeechMirror.HostEnd(Speech);
                if (ended.Serial != 0)
                {
                    host.CloseItem(ended.Serial);
                }

                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: the host's campaign tip \"{ended.Tip.Id}\" ended after {now - ended.Since:0.0} s{(ended.Serial != 0 ? $"; friends' end with it (#{ended.Serial})" : string.Empty)}."));
                return;
        }

        if (_shown is not { } shown)
        {
            return;
        }

        if (shown is { Serial: 0, BoardFull: false } && friends)
        {
            shown.Serial = host.OpenItem(CampaignTipData.Type, CampaignTipData.Type, shown.Tip.ToData());
            shown.BoardFull = shown.Serial == 0;
            ModLog.Info(shown.Serial != 0
                ? $"Menus: the host's campaign tip \"{shown.Tip.Id}\" goes to friends (#{shown.Serial}); each plays it in its own language."
                : $"Menus: the host's campaign tip \"{shown.Tip.Id}\" can't go to friends ({MenuBoard.MaxOpen} menus are open).");
        }

        if (shown.Serial != 0)
        {
            // The speech's progress on the tip's item (right after its open too: a friend who joined mid-tip).
            int value = SpeechMirror.HostValue(Speech, Speaker);
            if (value > shown.Pushed)
            {
                shown.Pushed = value;
                host.ItemValue(shown.Serial, SpeechProgress.Group, Speaker, value);
            }
        }
    }

    /// <summary>Host: the tip's speaker starts being followed, before its first line (its overlay comes first, and fades in).</summary>
    private static void BeginHostSpeech(HostTip tip)
    {
        CampaignDialoguePresenter? presenter = HostPresenter.Get();
        if (Bubbles(presenter) is { } bubbles && Anchor(presenter) is { } anchor)
        {
            SpeechMirror.HostBegin(Speech, Speaker, bubbles, anchor, Who(tip.Tip.Id));
        }
        else
        {
            ModLog.Warn($"Menus: the host's campaign tip \"{SafeText.Clean(tip.Tip.Id, 48)}\" has no speaker to follow; friends stay on its first line until it ends.");
        }
    }

    private static void FriendStep(FriendTip tip)
    {
        double now = FrameClock.Seconds;
        bool onItsScene = tip.Scene.Length == 0 || tip.Started || tip.HostEnded || MenuGame.SceneName() == tip.Scene;
        bool onItsScreen = !tip.Started && !tip.HostEnded && onItsScene && FriendPresenter.Get() != null;
        bool overlayUp = tip.Started && tip.HostEnded && FriendPresenter.OverlayUp();
        string id = tip.LogId;

        // The wait counts from this PC's arrival on the tip's scene: a friend who joined mid-tip may load for long,
        // and the host's end still ends it, so nothing waits for ever.
        tip.WaitFrom = onItsScene ? tip.WaitFrom : now;
        switch (CampaignTipTimeline.Friend(tip.Started, tip.HostEnded, onItsScreen, overlayUp, tip.WaitFrom, tip.EndedAt, now))
        {
            case FriendTipStep.Play:
                Play(tip);
                break;
            case FriendTipStep.LeftOut:
                ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" waited {CampaignTipTimeline.StartWaitSeconds:0} s for this PC to be on its screen " +
                    $"(\"{SafeText.Clean(tip.Scene, 48)}\" with a speech presenter); left out.");
                _tip = null;
                break;
            case FriendTipStep.NeverShown:
                ModLog.Info($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" ended before this PC was on its screen; not shown here.");
                _tip = null;
                break;
            case FriendTipStep.Done:
                SpeechMirror.FriendEnd(Speech);
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" ended there; this PC's ended with it ({now - tip.EndedAt:0.0} s later)."));
                _tip = null;
                break;
            case FriendTipStep.TakeDown:
                TakeDown(tip, $"still up {CampaignTipTimeline.RunOutSeconds:0} s after {LogNames.HostTag()}'s ended");
                SpeechMirror.FriendEnd(Speech);
                _tip = null;
                break;
            case FriendTipStep.RunOut:
                // The game's own per-anchor skip, as the mod's own press (a followed bubble refuses any other): typing shows in
                // full, a shown line goes on; the speech ends as it ends itself.
                CampaignDialoguePresenter? presenter = FriendPresenter.Get();
                if (Bubbles(presenter) is { } bubbles && Anchor(presenter) is { } anchor)
                {
                    SpeechMirror.FriendPress(bubbles, anchor);
                }

                break;
        }

        if (ReferenceEquals(_tip, tip) && tip.Started)
        {
            // The host's newest, read from the tip's own item (what it last said, once that has closed): every frame, since a
            // value before the speaker's start is lost. This PC's bubble follows it (SpeechMirror.FriendTick).
            SpeechMirror.FriendHostValue(Speech, Speaker, SpeechMirror.HostProgress(tip.Menu, Speaker).Value);
        }
    }

    /// <summary>The blacksmith's speech anchor (a tip's speaker), or null without a presenter.</summary>
    private static SpeechBubbleAnchor? Anchor(CampaignDialoguePresenter? presenter)
    {
        SpeechBubbleAnchor? anchor = presenter == null ? null : presenter.blacksmith?.anchor;
        return anchor != null ? anchor : null;
    }

    /// <summary>The controller the blacksmith's speech runs on: its own (verified in the game's code: <c>SpeechActor.PlaySpeech</c>
    /// shows it on <c>bubbleController</c>), or null.</summary>
    private static SpeechBubbleController? Bubbles(CampaignDialoguePresenter? presenter)
    {
        SpeechBubbleController? bubbles = presenter == null ? null : presenter.blacksmith?.bubbleController;
        return bubbles != null ? bubbles : null;
    }

    /// <summary>A tip's speaker, for the speech mirror's log lines.</summary>
    private static string Who(string id) => $"blacksmith tip \"{SafeText.Clean(id, 48)}\"";

    /// <summary>This PC's copy of the host's tip <paramref name="serial"/> (showing, or waiting its turn), or null.</summary>
    private static FriendTip? Of(uint serial) => _tip?.Serial == serial ? _tip : _next?.Serial == serial ? _next : null;

    /// <summary>The host's tip ended (its close, or a sync without it): this PC's runs out from the next frame.</summary>
    private static void Ended(FriendTip tip)
    {
        if (!tip.HostEnded)
        {
            tip.HostEnded = true;
            tip.EndedAt = FrameClock.Seconds;
        }
    }

    private static void EndedIfGone(FriendTip? tip, MenuMirror mirror)
    {
        if (tip != null && !mirror.TryGet(tip.Serial, out _))
        {
            Ended(tip);
        }
    }

    /// <summary>Plays the host's tip with the presenter's own call (tried once: what can't play is logged and left out).</summary>
    private static void Play(FriendTip tip)
    {
        CampaignDialoguePresenter? presenter = FriendPresenter.Get();
        string id = tip.LogId;
        tip.Started = true;
        string problem = "no speech presenter";
        SpeechSequence? sequence = presenter == null ? null : FindSequence(tip.Data.Id, out problem);
        if (presenter == null || sequence == null)
        {
            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" can't play here ({problem}); left out.");
            _tip = null;
            return;
        }

        if (tip.Data.Sequence.Length > 0 && sequence.name != tip.Data.Sequence)
        {
            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" is sequence \"{SafeText.Clean(tip.Data.Sequence, 48)}\" there, \"{SafeText.Clean(sequence.name, 48)}\" here; left out.");
            _tip = null;
            return;
        }

        // Followed from its first line (begun before the speech starts, as the mirror asks; one who joined mid-tip catches up).
        if (Bubbles(presenter) is { } bubbles && Anchor(presenter) is { } anchor)
        {
            SpeechMirror.FriendBegin(Speech, Speaker, bubbles, anchor, Who(tip.Data.Id));
        }
        else
        {
            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" has no speaker here to follow; it stays on its first line until {LogNames.HostTag()}'s ends.");
        }

        _ = presenter.PlayBlacksmithSpeech(sequence, tip.Data.Dark);
        int lines = sequence.Lines?.Count ?? 0;
        ModLog.Info($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" plays here ({lines} line(s), in this PC's language; nothing is marked seen here)" +
            $"{(lines != tip.Data.Lines ? $"; {tip.Data.Lines} line(s) there" : string.Empty)}.");
    }

    /// <summary>The tip <paramref name="id"/>'s sequence in this PC's own tip list (read directly: the lookup gives the stand-in while following), or null with why.</summary>
    private static SpeechSequence? FindSequence(string id, out string problem)
    {
        foreach (UnityEngine.Object any in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CampaignTipsConfig>()))
        {
            Il2CppSystem.Collections.Generic.List<TipDefinition>? tips = any?.TryCast<CampaignTipsConfig>()?.tips;
            for (int i = 0; tips != null && i < tips.Count; i++)
            {
                TipDefinition? tip = tips[i];
                if (tip != null && tip.id == id)
                {
                    problem = tip.sequence == null ? "its tip has no speech here" : string.Empty;
                    return tip.sequence;
                }
            }
        }

        problem = "this game has no tip by that id";
        return null;
    }

    /// <summary>Takes this PC's tip down at once: its bubbles cleared, its overlay hidden, the blacksmith sent off. Logged.</summary>
    private static void TakeDown(FriendTip tip, string why)
    {
        string id = tip.LogId;
        try
        {
            if (FriendPresenter.Get() is { } presenter)
            {
                if (Bubbles(presenter) is { } bubbles && Anchor(presenter) is { } anchor)
                {
                    bubbles.Clear(anchor);
                }

                // A cleared speech ends in an error, which skips the overlay's fade-out (its input block is restored in its own
                // finally, verified in the game's code): the overlay goes as its fade-out leaves it, and the blacksmith leaves.
                FriendPresenter.HideOverlay();
                if (presenter.blacksmith is { _isShowing: true } blacksmith)
                {
                    _ = blacksmith.Hide();
                }
            }

            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" was taken down here ({why}).");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: {LogNames.HostTag()}'s campaign tip \"{id}\" couldn't be taken down here ({why}; {ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The game's speech presenter and its dark overlay's object, held while they're alive (the campaign's installer makes it
    /// once and it outlives scenes, verified in the game's code at <c>Awake</c>'s <c>DontDestroyOnLoad</c>; looked for at
    /// most every <see cref="LookEverySeconds"/> while there's none), so a frame's look at the overlay makes no object.
    /// </summary>
    private sealed class PresenterCache
    {
        private const double LookEverySeconds = 0.25;

        private CampaignDialoguePresenter? _presenter;
        private GameObject? _overlay;
        private double _lookedAt = double.NegativeInfinity;

        /// <summary>The presenter, or null while the game has none.</summary>
        public CampaignDialoguePresenter? Get()
        {
            if ((_presenter == null || _presenter.WasCollected) && FrameClock.Seconds - _lookedAt >= LookEverySeconds)
            {
                _lookedAt = FrameClock.Seconds;
                CampaignDialoguePresenter? found = UnityEngine.Object.FindObjectOfType<CampaignDialoguePresenter>();
                _presenter = found != null ? found : null;
                UnityEngine.UI.Image? image = _presenter == null ? null : _presenter.darkOverlay;
                _overlay = image != null ? image.gameObject : null;
            }

            return _presenter != null && !_presenter.WasCollected ? _presenter : null;
        }

        /// <summary>True while the presenter's dark overlay shows (a blacksmith's speech is on).</summary>
        public bool OverlayUp() => Get() != null && _overlay != null && _overlay.activeSelf;

        /// <summary>Hides the overlay (as its own fade-out leaves it).</summary>
        public void HideOverlay()
        {
            if (Get() != null && _overlay != null)
            {
                _overlay.SetActive(false);
            }
        }

        /// <summary>Forgets the presenter (it's looked for again when next needed).</summary>
        public void Forget()
        {
            _presenter = null;
            _overlay = null;
            _lookedAt = double.NegativeInfinity;
        }
    }

    /// <summary>Host: a tip the game looked up to show, when, and how many frames it has waited since.</summary>
    private sealed record Armed(CampaignTipData Tip, double At)
    {
        public int Frames { get; set; }
    }

    /// <summary>Host: a tip that's up, since when, and its board item (0: not on the board yet).</summary>
    private sealed class HostTip
    {
        public HostTip(CampaignTipData tip, double since)
        {
            Tip = tip;
            Since = since;
        }

        public CampaignTipData Tip { get; }

        public double Since { get; }

        public uint Serial { get; set; }

        public bool BoardFull { get; set; }

        /// <summary>The newest progress of its speech on its item (a speech's only goes up).</summary>
        public int Pushed { get; set; }
    }

    /// <summary>Friend: the host's tip as this PC shows it.</summary>
    private sealed class FriendTip
    {
        public FriendTip(MirroredMenu menu, CampaignTipData data, double arrivedAt)
        {
            Menu = menu;
            Serial = menu.State.Serial;
            Data = data;
            Scene = menu.State.Scene;
            WaitFrom = arrivedAt;
            LogId = SafeText.Clean(data.Id, 48);
        }

        /// <summary>Its item as mirrored here, kept for its speech's newest value (read each frame with no search; what it last said stays once it has closed).</summary>
        public MirroredMenu Menu { get; set; }

        public uint Serial { get; }

        public CampaignTipData Data { get; }

        public string Scene { get; }

        public string LogId { get; }

        /// <summary>What its wait to start counts from: when it came, then each frame this PC isn't on its scene yet.</summary>
        public double WaitFrom { get; set; }

        public bool Started { get; set; }

        public bool HostEnded { get; set; }

        public double EndedAt { get; set; }
    }
}
#endif
