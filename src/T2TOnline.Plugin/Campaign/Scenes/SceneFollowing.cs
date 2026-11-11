#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppBishopGames.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// Scene following: where the host's game is in the campaign's scenes, and friends' games following it there.
/// <list type="bullet">
/// <item>Host: publishes <see cref="Screens.Title"/> on the title screen (no run needed; with its button group and the
/// highlighted title button, <see cref="TitleCursor"/>, republished on change), <see cref="Screens.Map"/> once
/// the world map has loaded and the game saved the run there (so the checkpoint holds the act's map, D44), and
/// <see cref="Screens.Preview"/> while a match node's preview is open (back to the map when it closes without a match).
/// The map view carries the cursor: the node it's on and the camera's scroll target, republished on change (the node at
/// most 10 times a second, the camera 4). Only a shared campaign is followed: while the host's game plays its solo run
/// (<see cref="RunSync.HostOnCoopSlot"/> false: hosting started mid-run, D46) friends get the title view wherever the host
/// is. Campaign matches (Match/CampaignMatch.cs) and menus (Campaign/Menus/) are other modules'.</item>
/// <item>Friend: the title, map and preview followers; the friend's own UI locked while it follows
/// (<see cref="FriendUi"/>); the map's commands and rolls refused (<see cref="MapGuards"/>).</item>
/// </list>
/// </summary>
internal sealed class SceneFollowing : ICampaignModule, IScreenFollowerSource
{
    /// <summary>The cursor's node goes out at most this often.</summary>
    private const long CursorIntervalMicros = 100_000;

    /// <summary>The camera's target goes out at most this often (it changes every frame while the host pans).</summary>
    private const long CameraIntervalMicros = 250_000;

    /// <summary>The camera's target is rounded to this before it's compared and sent.</summary>
    private const float CameraStep = 0.1f;

    /// <summary>How often the cursor and camera are read at most: faster than either goes out, slower than every frame (a node entry is noticed at once).</summary>
    private const double CursorLookSeconds = 0.05;

    /// <summary>After this long waiting for a run checkpoint for its map, the host's log says so (once).</summary>
    private const double SlowCheckpointSeconds = 5;

    private readonly CampaignContext _context;
    private readonly TitleFollower _title;
    private readonly MapFollower _map;
    private readonly PreviewFollower _preview;
    private readonly RepublishGate _cursorGate = new(CursorIntervalMicros);
    private readonly RepublishGate _cameraGate = new(CameraIntervalMicros);
    private CampaignRelay? _relay;
    private HostScreen _screen;
    private string _scene = string.Empty;
    private int _sceneHandle;
    private double _nextCursorLook;
    private int _savedFrame = -1;
    private double _savedAt;
    private bool _slowLogged;
    private MatchPreviewMenu? _previewOpened;
    private bool _previewClosed;
    private PreviewViewDetails? _publishedPreview;
    private MapViewDetails? _published;
    private MapPoint? _selected;
    private string? _relic;
    private int? _relicAt;
    private IntPtr _lastNavigable;
    private bool _saveOnMap;
    private bool _soloLogged;
    private TitleViewDetails? _titlePublished;

    /// <summary>Made once by <see cref="CampaignModules"/>.</summary>
    public SceneFollowing(CampaignContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _title = new TitleFollower(context.Matches);
        _map = new MapFollower(context.Matches);
        _preview = new PreviewFollower(context.Matches);
        PreviewMirror.Opened += menu => _previewOpened = menu;
        PreviewMirror.Closed += () => _previewClosed = true;
        PreviewMirror.Started += _ => PublishZoom();
        Instance = this;
    }

    private enum HostScreen
    {
        None,
        Title,
        Map,
        Preview,
    }

    /// <summary>The module of the running game, or null before it's made.</summary>
    public static SceneFollowing? Instance { get; private set; }

    /// <inheritdoc />
    public string Name => "Scenes";

    /// <inheritdoc />
    public IEnumerable<IScreenFollower> Followers => new IScreenFollower[] { _title, _map, _preview };

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay)
    {
        _relay = relay;
        ResetHost();
        _scene = WorldMap.SceneName;
        _sceneHandle = WorldMap.SceneHandle;
        switch (_scene)
        {
            case WorldMap.MapScene:
                // Friends need a checkpoint from inside the map: a shared run is saved once here (as the game's next save
                // would), and the view goes out with that checkpoint.
                _savedFrame = UnityEngine.Time.frameCount;
                _savedAt = FrameClock.Seconds;
                _saveOnMap = true;
                ModLog.Info("Scenes: hosting from the world map; friends follow it once there's a run checkpoint from it.");
                break;
            case WorldMap.MatchScene:
                ModLog.Info("Scenes: hosting from a match; the match decides what friends see next.");
                break;
            default:
                PublishTitle();
                break;
        }
    }

    /// <inheritdoc />
    public void StopHosting(string reason)
    {
        ActIntro.HostStopped();
        MapReveal.HostStopped();
        ResetHost();
    }

    /// <inheritdoc />
    public void StartFollowing(CampaignRelay relay)
    {
        _relay = relay;
        ActIntro.NewSession();
        NodeEntryMirror.ResetFriend();
        NodeTooltipMirror.Reset();
        RelicBarMirror.Reset();
        FriendUi.Lock($"following {LogNames.Tag(relay.FollowedHost)}"); // a log line's words (D127)
    }

    /// <inheritdoc />
    public void StopFollowing(string reason, bool gameClosing)
    {
        // The UI lock comes off on every way out of here (a failure included), unless the session's exit keeps it until
        // this game's own title screen is in (SessionExit ends it then, or after its time-out).
        bool exiting = false;
        try
        {
            _preview.ForgetZoom();
            NodeEntryMirror.Leave();
            NodeEntryMirror.ResetFriend();
            NodeTooltipMirror.Reset();
            ModLog.Info($"Scenes: stopped following ({reason}); {_map.CursorMoves} map cursor move(s) mirrored, {MapGuards.Refusals} unexpected map command(s) refused " +
                $"and {MapGuards.ExpectedRefusals} mirrored zoom end(s) held back so far; {NodeEntryMirror.Summary}; {NodeTooltipMirror.Summary}; {RelicBarMirror.Summary}; " +
                $"{GameTips.Skipped} game tip(s) skipped.");
            RelicBarMirror.Reset();
            if (!gameClosing && WorldMap.SceneName == WorldMap.MapScene)
            {
                // Run sync puts this PC's own run back as the session ends (it stops last), so the map on screen is the host's
                // with nothing behind it: back to the title screen, which shows this player's own campaign again. Until it's there the UI
                // lock and the map guards stay on (a node entered now would act on this game's own run).
                string problem;
                try
                {
                    exiting = WorldMap.Load(WorldMap.TitleScene, out problem);
                }
                catch (Exception ex)
                {
                    problem = $"{ModLog.SafeErrorText(ex)}";
                }

                if (exiting)
                {
                    SessionExit.Begin("the session ended on the host's world map");
                    exiting = SessionExit.Leaving;
                    ModLog.Info("Scenes: back to the title screen (the session ended on the host's world map).");
                    return;
                }

                ModLog.Warn($"Scenes: couldn't go back to the title screen after the session ({problem}); quit to the title from the map's menu.");
            }
        }
        finally
        {
            if (!exiting)
            {
                FriendUi.Unlock(reason);
            }
        }
    }

    /// <inheritdoc />
    public void Update()
    {
        if (_relay?.Hosting == true)
        {
            HostUpdate();
        }

        if (_relay?.Following == true)
        {
            FriendUi.Update();
            _preview.Tick();
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Status()
    {
        if (_relay?.Following == true && _relay.View?.Screen == Screens.Title && !_context.Matches.FriendBusy)
        {
            yield return $"waiting for {SafeText.Clean(_relay.HostName, 32)} to start the campaign";
        }
    }

    /// <summary>Host: the game saved its run (<c>CampaignState.SaveRunData</c>).</summary>
    internal static void SaveRunDataPostfix()
    {
        try
        {
            SceneFollowing? scenes = Instance;
            if (scenes?._relay?.Hosting == true && WorldMap.SceneName == WorldMap.MapScene)
            {
                // Published from the next frame's update: by then run sync has published this save's checkpoint.
                scenes._savedFrame = UnityEngine.Time.frameCount;
                scenes._savedAt = FrameClock.Seconds;
                scenes._slowLogged = false;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: noting the host's save failed", ex);
        }
    }

    private void HostUpdate()
    {
        // The scene's name only when its handle changed (one call a frame, not a new string a frame, in a match too).
        int handle = WorldMap.SceneHandle;
        if (handle != _sceneHandle)
        {
            _sceneHandle = handle;
            string scene = WorldMap.SceneName;
            if (scene != _scene)
            {
                _scene = scene;
                HostSceneChanged(scene);
            }
        }

        if (RunSync.Instance?.HostOnCoopSlot != true)
        {
            // The host's game plays its solo run: nothing of it is shared, so friends wait at the title (run sync tells the
            // host to go to the title screen, where the co-op slot takes over).
            _savedFrame = -1;
            _previewOpened = null;
            _previewClosed = false;
            if (_screen != HostScreen.Title)
            {
                PublishTitle();
                if (!_soloLogged)
                {
                    _soloLogged = true;
                    ModLog.Info($"Scenes: the host's game plays its solo run, not the co-op slot, so friends see the title view (the host is on {_scene}).");
                }
            }

            return;
        }

        _soloLogged = false;
        if (_saveOnMap && WorldMap.MapLoaded && _relay!.LatestRun == null && PreviewMirror.Menu is not { IsShown: true })
        {
            // Hosting started on the shared run's map: one save, as the game makes at every map load, gives friends the run.
            _saveOnMap = false;
            ModLog.Info("Scenes: saving the host's shared run once, so friends get a checkpoint of this map.");
            try
            {
                WorldMap.State?.SaveRunData();
            }
            catch (Exception ex)
            {
                ModLog.Error("Scenes: saving the host's run failed (friends wait for the game's next save)", ex);
            }
        }

        if (_previewOpened is MatchPreviewMenu opened)
        {
            _previewOpened = null;
            _previewClosed = false;
            PublishPreview(opened);
        }

        if (_previewClosed)
        {
            _previewClosed = false;
            if (_screen == HostScreen.Preview)
            {
                PublishMap("the host closed its preview");
            }
        }

        if (_savedFrame >= 0 && UnityEngine.Time.frameCount > _savedFrame && WorldMap.MapLoaded)
        {
            if (_relay!.LatestRun == null || RunSync.Instance?.CheckpointPending == true)
            {
                // No checkpoint yet, or this save's checkpoint is still on its way (run sync reads the file again until it
                // changed): the view waits for it, else friends would load the run before this save (after a boss, the
                // previous act, whose map they mustn't generate).
                if (!_slowLogged && FrameClock.Seconds - _savedAt > SlowCheckpointSeconds)
                {
                    _slowLogged = true;
                    ModLog.Warn(_relay.LatestRun == null
                        ? "Scenes: the host's world map has no run checkpoint yet (run sync sends one at the game's next save); friends wait for it."
                        : "Scenes: the host's newest save hasn't become a run checkpoint yet; friends wait for it.");
                }
            }
            else
            {
                _savedFrame = -1;
                if (_screen != HostScreen.Preview)
                {
                    PublishMap("the host saved its run on the map");
                }
            }
        }

        if (ActIntro.TakeStarted() && _screen == HostScreen.Map && _published is { Intro: not null } held)
        {
            // The act intro held for friends started: their copies start with it.
            Publish(held with { IntroStarted = true });
        }

        if (_screen == HostScreen.Map)
        {
            // A checkpoint published after the map's view (a save read again once it reached the disk): the view follows it.
            if (_relay!.LatestRun is { } latest && _relay.View is { Screen: Screens.Map } view && view.Checkpoint != latest.Sequence && WorldMap.MapLoaded)
            {
                PublishMap($"run checkpoint {latest.Sequence}");
            }

            TrackCursor();
        }

        if (_screen == HostScreen.Title)
        {
            TrackTitle();
        }
    }

    private void HostSceneChanged(string scene)
    {
        _published = null;
        _publishedPreview = null;
        _selected = null;
        (_relic, _relicAt) = (null, null);
        _lastNavigable = IntPtr.Zero;
        _saveOnMap = false;
        _screen = HostScreen.None;
        if (scene == WorldMap.TitleScene)
        {
            _savedFrame = -1;
            PublishTitle();
        }
    }

    private void PublishTitle() => PublishTitle(TitleCursor.HostDetails());

    private void PublishTitle(TitleViewDetails title)
    {
        var details = new List<KeyValuePair<string, NetValue>> { new(Details.Scene, NetValue.FromString(WorldMap.TitleScene)) };
        details.AddRange(title.ToValues());
        _relay!.PublishView(Screens.Title, details, needsRun: false);
        _titlePublished = title;
        _screen = HostScreen.Title;
    }

    /// <summary>
    /// The host's title: republished when its button group changes (at once) or its cursor moves to another title button
    /// (within the cursor's gate).
    /// </summary>
    private void TrackTitle()
    {
        double seconds = FrameClock.Seconds;
        if (seconds < _nextCursorLook)
        {
            return;
        }

        _nextCursorLook = seconds + CursorLookSeconds;
        TitleViewDetails now = TitleCursor.HostDetails();
        if (now == _titlePublished)
        {
            return;
        }

        if (now.Group != _titlePublished?.Group || _cursorGate.TryPass(FrameClock.Micros))
        {
            PublishTitle(now);
        }
    }

    private void PublishMap(string why)
    {
        MapPlace? place = WorldMap.Place();
        if (place == null)
        {
            ModLog.Warn($"Scenes: couldn't read the host's world map ({why}); friends stay where they are.");
            return;
        }

        (MapPoint? selected, float? camera) = ReadCursor();
        var details = new MapViewDetails(place.RunId, place.Act, place.Path, selected, camera)
        {
            Intro = ActIntro.HostIntro(place),
            IntroStarted = ActIntro.HostIntroStarted(place),
            Entering = NodeEntryMirror.HostEntering(),
            TooltipShown = NodeTooltipMirror.HostShows(selected),
            Relic = _relic,
            RelicAt = _relicAt,
        };
        if (_published == null || !_published.SamePlace(details))
        {
            ModLog.Info($"Scenes: the host's world map: act {details.Act}, node {details.Node}, {details.Path.Count} node(s) visited this act ({why})" +
                $"{(details.Intro is int intro ? $"; act {intro}'s intro played as it was made" : string.Empty)}.");
        }

        Publish(details);
    }

    private void PublishPreview(MatchPreviewMenu menu)
    {
        MapPlace? place = WorldMap.Place();
        PreviewViewDetails? preview = null;
        string problem = "no world map";
        if (place == null || !PreviewMirror.TryCapture(menu, place, out preview, out problem))
        {
            ModLog.Warn($"Scenes: couldn't read the host's match preview ({problem}); friends stay on the map.");
            return;
        }

        _relay!.PublishView(Screens.Preview, preview!.ToValues());
        _publishedPreview = preview;
        _screen = HostScreen.Preview;
        ModLog.Info($"Scenes: the host's preview of node {preview.Target} (type {preview.NodeType}): opponent {string.Join(", ", preview.Opponent)}; " +
            $"goalie {preview.Goalie}; possible rewards: relic \"{preview.RewardRelic}\", talent \"{preview.RewardTalent}\", ability \"{preview.RewardAbility}\".");
    }

    /// <summary>
    /// The host started the match from the preview it published: the same preview again, now zooming, so each friend's
    /// zoom plays alongside the host's and ends before the friend's copy of the match loads over its map.
    /// </summary>
    private void PublishZoom()
    {
        if (_relay == null || _screen != HostScreen.Preview || _publishedPreview == null || _publishedPreview.Zooming)
        {
            return;
        }

        _publishedPreview = _publishedPreview with { Zooming = true };
        _relay.PublishView(Screens.Preview, _publishedPreview.ToValues());
        ModLog.Info($"Scenes: the host started the match from its preview of node {_publishedPreview.Target}; friends play the same zoom now.");
    }

    private void Publish(MapViewDetails details)
    {
        HostView? view = _relay!.PublishView(Screens.Map, details.ToValues());
        if (view != null)
        {
            ActIntro.HostViewPublished(details, view.Epoch);
            MapReveal.HostViewPublished(view.Epoch);
        }

        _published = details;
        _screen = HostScreen.Map;
    }

    /// <summary>
    /// The host's cursor: republished when the node it's on, its details or the camera's target changes, within the gates;
    /// a node entry starting or ending goes out at once (friends' zooms start with the host's).
    /// </summary>
    private void TrackCursor()
    {
        if (_published == null)
        {
            return;
        }

        NodeEntry? entering = NodeEntryMirror.HostEntering();
        bool entry = entering != _published.Entering;
        double seconds = FrameClock.Seconds;
        if (!entry && seconds < _nextCursorLook)
        {
            return;
        }

        _nextCursorLook = seconds + CursorLookSeconds;
        (MapPoint? selected, float? camera) = ReadCursor();
        bool? tooltip = NodeTooltipMirror.HostShows(selected);
        bool node = selected != _published.Selected || tooltip != _published.TooltipShown || _relic != _published.Relic || _relicAt != _published.RelicAt;
        bool scroll = camera != _published.CameraX;
        long now = FrameClock.Micros;
        if (entry || (node && _cursorGate.TryPass(now)) || (!node && scroll && _cameraGate.TryPass(now)))
        {
            Publish(_published with { Selected = selected, CameraX = camera, Entering = entering, TooltipShown = tooltip, Relic = _relic, RelicAt = _relicAt });
        }
    }

    /// <summary>The map node the cursor is on (the last one, while it's on something else) and the camera's target, rounded.</summary>
    private (MapPoint? Selected, float? Camera) ReadCursor()
    {
        AbstractNavigable? current = WorldMap.Navigation?.CurrentNavigable;
        if (current != null && current.Pointer != _lastNavigable)
        {
            _lastNavigable = current.Pointer;
            if (WorldMap.PointOf(current) is MapPoint point)
            {
                _selected = point;
                (_relic, _relicAt) = (null, null);
            }
            else
            {
                // In the map's relic bar (RelicBarMirror), or in something else (a menu): the relic, or none.
                (_relic, _relicAt) = RelicBarMirror.HostRelic(current);
            }
        }

        float? camera = WorldMap.CameraTarget is float x && float.IsFinite(x) ? MathF.Round(x / CameraStep) * CameraStep : null;
        return (_selected, camera);
    }

    private void ResetHost()
    {
        NodeEntryMirror.ClearHost();
        RelicBarMirror.ResetHost();
        _screen = HostScreen.None;
        _savedFrame = -1;
        _slowLogged = false;
        _previewOpened = null;
        _previewClosed = false;
        _publishedPreview = null;
        _published = null;
        _selected = null;
        (_relic, _relicAt) = (null, null);
        _lastNavigable = IntPtr.Zero;
        _saveOnMap = false;
        _soloLogged = false;
        _titlePublished = null;
        _cursorGate.Reset();
        _cameraGate.Reset();
    }
}
#endif
