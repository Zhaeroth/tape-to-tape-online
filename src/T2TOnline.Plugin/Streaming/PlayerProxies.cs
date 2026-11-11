#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSkaters;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using GroundMarkers = Il2CppUI.SkaterGroundMarkers;
using Player = Il2CppTape2Tape.Player;
using PlayerIndicator = Il2CppUI.PlayerIndicator;
using Team = Il2CppTape2Tape.Team;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The players' colour markers on a friend's PC. The markers the game draws in a
/// player's colour ask <c>PlayersManager.IsSkaterControlled(skater, out player)</c> which player controls a skater: the
/// off-screen box (<c>UI.PlayerIndicator</c> and its <c>OffscreenTarget</c>) and the star over the skater
/// (<c>SkaterInitUI</c>/<c>SkaterUI</c>, in the CPU colour when nobody controls it); a squad's ring on the ice
/// (<c>SkaterGroundMarkers</c>) is placed from each team's player list. A friend's game has no players of its own
/// (everyone plays on the host), so every answer was "nobody".
/// <list type="bullet">
/// <item>Host: every player's id, Rewired player, controlled skater and star colours go in the HUD state
/// (<c>players</c>, <c>player.&lt;id&gt;.*</c>).</item>
/// <item>Friend: a stand-in <c>Player</c> per host player, on an inactive object of its own (so none of its code ever
/// runs), carries the same id, skater and colours. While playing, once the stream has listed the players,
/// <c>IsSkaterControlled</c> answers from them and the ground markers are placed from them.</item>
/// <item>The game draws these markers only when an event tells it to (a player taking or leaving a skater, the puck
/// changing hands). The host's players took their skaters before a friend's stream started (at the match's start, or
/// before a friend rejoined), so the friend's widgets were drawn while nobody controlled anything: no off-screen box, the
/// star in the CPU colour. Once per playback, as soon as the stream has listed every player with a colour and the game
/// state is one where the host shows markers, they're redrawn through the game's own code (<c>FillStar</c> or
/// <c>RemoveStar</c>, <c>RefreshPlayerControl</c> and <c>RefreshState</c>, <c>RefreshActiveSkater</c>), as those events
/// would have drawn them. After that, once a second, every shown off-screen box and visible star is compared with the
/// colour of the stand-in controlling its skater; one found wrong in two checks in a row (a HUD value arrives just before
/// the event that redraws its marker, so one check can fall between them) is redrawn the same way, and the first few are
/// logged.</item>
/// </list>
/// The patches go in at the first playback, not at start-up: gameplay asks <c>IsSkaterControlled</c> thousands of times
/// a second, and the host's own play shouldn't pay for a patch only a friend uses. Once in, they stay for the session (a
/// host that rehearsed with F2 or F4 keeps them): whether HarmonyX can take an IL2CPP detour out again cleanly isn't
/// established, so it isn't tried. Outside playback the prefixes return at once. <c>GetPlayerFromSkater</c> is left alone
/// on purpose: the NHL94 aim indicator would keep the stand-in it returns and read its input every frame.
/// </summary>
internal static class PlayerProxies
{
    private const double CheckEverySeconds = 1;
    private const double FindEverySeconds = 5;
    private const int MaxFixLogs = 3;
    private const int MaxFailureLogs = 3;

    private static readonly Dictionary<int, Player> Proxies = new();
    private static readonly Dictionary<int, IntPtr> SkaterOf = new();
    private static readonly Dictionary<int, Skater> SkaterObjectOf = new();
    private static readonly Dictionary<int, int> RewiredOf = new();
    private static readonly HashSet<int> Coloured = new();
    private static readonly List<int> ProxyIds = new();
    private static readonly List<PlayerIndicator> Indicators = new();
    private static readonly List<SkaterInitUI> SkaterUis = new();
    private static readonly MarkerWatch<IntPtr, (MarkerFix Fix, ColorValue Colour)> BoxWatch = new();
    private static readonly MarkerWatch<IntPtr, ColorValue> StarWatch = new();
    private static readonly MarkerWatch<IntPtr, FaceIconFix> FaceWatch = new();
    private static readonly Dictionary<IntPtr, int> FaceRetries = new();
    private static readonly Dictionary<IntPtr, GameObject> HiddenIcons = new();
    private static readonly string[][] HostKeys = new string[PlayerMarkers.MaxPlayerId + 1][];
    private static readonly StringBuilder ListText = new();
    private static string _lastList = string.Empty;
    private static HarmonyLib.Harmony? _harmony;
    private static bool _active;
    private static bool _listed;
    private static bool? _installed;
    private static bool _redrawPending;
    private static bool _redrawn;
    private static bool _groundNoted;
    private static double _nextCheck;
    private static double _nextFind;
    private static int _answered;
    private static int _groundFrames;
    private static int _boxesFixed;
    private static int _starsFixed;
    private static int _readFailures;
    private static bool _hostListDue; // a match is recording here whose players as sent to friends aren't logged yet (never set on a friend, whose playback reads its own players at its start)
    private static int _friendFailures;
    private static int _faceFixes;
    private static bool _faceReadout;

    /// <summary>
    /// This PC's Rewired player on the host (the slot the host gave this friend's controller), or -1: marks "you" in the
    /// log. Set when the friend session joins.
    /// </summary>
    public static int YourRewired { get; set; } = -1;

    /// <summary>Playback starts: the stream's players answer from now on (patching the first time), and the markers are redrawn once they're listed.</summary>
    public static void Begin()
    {
        Clear();
        _installed ??= Install();
        _active = _installed.Value;
        _answered = _groundFrames = _boxesFixed = _starsFixed = _friendFailures = _faceFixes = 0;
        _faceReadout = false;
        _redrawPending = true;
        _redrawn = false;
        _groundNoted = false;
        _nextCheck = _nextFind = 0;
    }

    /// <summary>Playback ends: this PC's own players answer again, and the stand-ins go.</summary>
    public static void End()
    {
        _active = false;
        if (Proxies.Count > 0)
        {
            ModLog.Info($"Player markers: {Proxies.Count} of the host's players mirrored (game player " +
                $"{string.Join(", ", Proxies.Keys.OrderBy(k => k))}); IsSkaterControlled answered from them {_answered} times; " +
                $"markers redrawn at the stream's start: {(_redrawn ? "yes" : "no (the stream never listed every player with a colour in a game state that shows markers)")}; " +
                $"corrected afterwards: {_boxesFixed} off-screen box(es), {_starsFixed} star(s) over a skater" +
                $"{(_faceFixes > 0 ? $", {_faceFixes} face icon(s) with no face (asked again {FaceRetries.Values.Sum()} time(s), {HiddenIcons.Count} hidden)" : string.Empty)}" +
                $"{(_groundFrames > 0 ? $"; ground markers placed from them in {_groundFrames} frames" : string.Empty)}.");
        }

        Clear();
    }

    /// <summary>The game player (id) the given Rewired player is on the host, or -1. Called from the status box each frame.</summary>
    public static int PlayerOfRewired(int rewiredId)
    {
        foreach ((int id, int rewired) in RewiredOf)
        {
            if (rewired == rewiredId)
            {
                return id;
            }
        }

        return -1;
    }

    /// <summary>The stand-in for host player <paramref name="id"/> once the stream has mentioned it, else null (none is made here).</summary>
    public static Player? ProxyOf(int id) => Proxies.TryGetValue(id, out Player? proxy) && proxy != null ? proxy : null;

    /// <summary>The marker colour of the host's player on Rewired player <paramref name="rewiredId"/>, once the stream said.</summary>
    public static bool TryMarkerColour(int rewiredId, out Color colour)
    {
        colour = default;
        int id = PlayerOfRewired(rewiredId);
        if (id < 0 || !Coloured.Contains(id) || !Proxies.TryGetValue(id, out Player? proxy) || proxy == null)
        {
            return false;
        }

        colour = proxy.playerStarColor.arrowColor;
        return colour.a > 0.1f;
    }

    /// <summary>A plain name for a marker colour ("orange"), for the status box.</summary>
    public static string ColourName(Color colour) => PlayerMarkers.ColourName(Value(colour));

    // ---------------------------------------------------------------- host

    /// <summary>Host: every player's state into the HUD values. A failure costs only the markers, never the rest of the HUD.</summary>
    public static void Read(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        try
        {
            ReadPlayers(values, refOf);
        }
        catch (Exception ex)
        {
            if (_readFailures++ < MaxFailureLogs)
            {
                ModLog.Warn($"Player markers: couldn't read the players: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    private static void ReadPlayers(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        PlayersManager? manager = PlayersManager.Instance;
        Il2CppSystem.Collections.Generic.List<Player>? players = manager != null ? manager._players : null;
        if (players == null)
        {
            return;
        }

        // Sampled 20 times a second: the keys are made once per id, and the list text only when it changes.
        ListText.Clear();
        bool allColoured = players.Count > 0;
        for (int i = 0; i < players.Count; i++)
        {
            Player? player = players[i];
            if (player == null)
            {
                continue;
            }

            int id = player._PlayerId_k__BackingField;
            int rewired = player.RewiredPlayer?.id ?? -1;
            ListText.Append(CultureInfo.InvariantCulture, $"{(ListText.Length > 0 ? "," : string.Empty)}{id}:{rewired}");
            if (id is < 0 or > PlayerMarkers.MaxPlayerId)
            {
                continue;
            }

            string[] keys = HostKeys[id] ??= new[] { $"player.{id}.skater", $"player.{id}.color", $"player.{id}.color.off" };
            Skater? skater = player.controlledSkater;
            values[keys[0]] = skater != null ? refOf(skater) : NetValue.Null;
            PlayerStarColor colors = player.playerStarColor;
            allColoured &= colors.arrowColor.a > 0.1f;
            values[keys[1]] = NetValue.FromColor(colors.arrowColor.r, colors.arrowColor.g, colors.arrowColor.b, colors.arrowColor.a);
            values[keys[2]] = NetValue.FromColor(colors.arrowColorUnselected.r, colors.arrowColorUnselected.g, colors.arrowColorUnselected.b, colors.arrowColorUnselected.a);
        }

        if (!ListText.Equals(_lastList.AsSpan()))
        {
            _lastList = ListText.ToString();
        }

        values["players"] = NetValue.FromString(_lastList);
        if (allColoured && _hostListDue)
        {
            _hostListDue = false;
            LogHostPlayers(manager!, players);
        }
    }

    /// <summary>Host: a new recorded match; its players are logged again once each has a colour.</summary>
    public static void HostMatchStarted() => _hostListDue = true;

    /// <summary>
    /// Host, once per recorded match when every player has a colour: the players as sent to friends, in the words of a
    /// friend's "the host's players here" line, so the two compare word for word.
    /// </summary>
    private static void LogHostPlayers(PlayersManager manager, Il2CppSystem.Collections.Generic.List<Player> players)
    {
        var text = new StringBuilder();
        for (int i = 0; i < players.Count; i++)
        {
            Player? player = players[i];
            if (player == null)
            {
                continue;
            }

            Skater? skater = player.controlledSkater;
            text.Append(text.Length > 0 ? ", " : string.Empty)
                .Append(CultureInfo.InvariantCulture, $"game player {player._PlayerId_k__BackingField} {PlayerMarkers.Describe(Value(player.playerStarColor.arrowColor))} ")
                .Append(skater != null ? $"on {LogNames.ObjectName(skater)}" : "with no skater")
                .Append(CultureInfo.InvariantCulture, $" (Rewired player {player.RewiredPlayer?.id ?? -1})");
        }

        ModLog.Info($"Player markers (host): this match's players as sent to friends: {(text.Length > 0 ? text.ToString() : "none")}; this PC's CPU colour " +
            $"{PlayerMarkers.Describe(Value(manager.cpuColor.arrowColor))}.");
    }

    // ---------------------------------------------------------------- friend

    /// <summary>Friend: one mirrored value. True when the key was a player key. Network input: bad values are ignored.</summary>
    public static bool Write(string key, NetValue value, Func<NetValue, Type, object?> resolve)
    {
        if (key == "players")
        {
            SetList(value.Kind == NetValueKind.Text ? value.Text : null);
            return true;
        }

        if (!key.StartsWith("player.", StringComparison.Ordinal))
        {
            return false;
        }

        // "player.<id>.skater", "player.<id>.color", "player.<id>.color.off".
        ReadOnlySpan<char> rest = key.AsSpan("player.".Length);
        int dot = rest.IndexOf('.');
        if (dot <= 0 || !int.TryParse(rest[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id > PlayerMarkers.MaxPlayerId)
        {
            return true;
        }

        Player? proxy = Proxy(id);
        if (proxy == null)
        {
            return true;
        }

        ReadOnlySpan<char> field = rest[(dot + 1)..];
        switch (field)
        {
            case "skater":
                Skater? skater = value.Kind == NetValueKind.Null ? null : resolve(value, typeof(Skater)) as Skater;
                proxy.controlledSkater = skater!;
                SkaterOf[id] = skater != null ? skater.Pointer : IntPtr.Zero;
                Remember(id, skater);
                break;
            case "color" or "color.off" when value.Kind == NetValueKind.Colour && new ColorValue(value.X, value.Y, value.Z, value.W).IsValid:
                PlayerStarColor colors = proxy.playerStarColor;
                var color = new Color(value.X, value.Y, value.Z, value.W);
                if (field.Length > "color".Length)
                {
                    colors.arrowColorUnselected = color;
                }
                else
                {
                    colors.arrowColor = color;
                    Coloured.Add(id);
                }

                proxy.playerStarColor = colors;
                break;
        }

        return true;
    }

    /// <summary>
    /// Friend, after each HUD message has been written: redraws the markers once per playback when the stream has listed
    /// every player with a colour and game state <paramref name="gameState"/> is one where the host shows them; after that,
    /// checks the drawn colours once a second. Never throws.
    /// </summary>
    public static void AfterHudMessage(int? gameState)
    {
        if (!_active || !_listed || gameState is not int state || !PlayerMarkers.ShownIn(state))
        {
            return;
        }

        try
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_redrawPending)
            {
                if (RewiredOf.Keys.All(Coloured.Contains))
                {
                    _redrawPending = false;
                    _redrawn = true;
                    _nextCheck = now + CheckEverySeconds;
                    Redraw(state, now);
                }

                return;
            }

            if (_redrawn && now >= _nextCheck)
            {
                _nextCheck = now + CheckEverySeconds;
                Check(state, now);
            }
        }
        catch (Exception ex)
        {
            Failed("redraw or check the markers", ex);
        }
    }

    private static void SetList(string? list)
    {
        _listed = true;
        RewiredOf.Clear();
        foreach (HostPlayer player in PlayerMarkers.ParseList(list))
        {
            RewiredOf[player.Id] = player.Rewired;
        }

        // A player who left the host's game controls nothing any more.
        foreach ((int id, Player proxy) in Proxies)
        {
            if (!RewiredOf.ContainsKey(id) && proxy != null)
            {
                proxy.controlledSkater = null!;
                SkaterOf[id] = IntPtr.Zero;
                Remember(id, null);
            }
        }
    }

    /// <summary>
    /// The markers redrawn once, as the game's own events would have drawn them had this PC seen the host's players take
    /// their skaters; then the players and their colours named in the log. Each widget on its own: one that fails costs
    /// only itself.
    /// </summary>
    private static void Redraw(int state, double now)
    {
        Find(now);
        int boxesShown = 0, boxesHidden = 0, stars = 0, panels = 0, failed = 0;
        foreach (PlayerIndicator indicator in Indicators)
        {
            try
            {
                Skater? skater = indicator != null && !indicator._isDisabled ? indicator.skater : null;
                if (skater == null)
                {
                    continue;
                }

                if (ControllerOf(skater.Pointer) != null)
                {
                    // Shown in the player's colour, or hidden while the skater has the puck, as the game does.
                    indicator!.FillStar();
                    boxesShown++;
                }
                else if (indicator!.offscreenTarget is OffscreenTarget box && box != null && box.enabled)
                {
                    indicator.RemoveStar();
                    boxesHidden++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Failed("redraw an off-screen box", ex);
            }
        }

        foreach (SkaterInitUI ui in SkaterUis)
        {
            try
            {
                if (ui == null)
                {
                    continue;
                }

                // What the game does when a player takes a skater (SkaterInitUI.OnPlayerTakeControl).
                ui.m_UI?.RefreshPlayerControl();
                ui.RefreshState();
                stars++;
            }
            catch (Exception ex)
            {
                failed++;
                Failed("redraw a star over a skater", ex);
            }
        }

        foreach (ActiveSkaterGlobalUI panel in UnityEngine.Object.FindObjectsOfType<ActiveSkaterGlobalUI>())
        {
            try
            {
                panel.RefreshActiveSkater();
                panels++;
            }
            catch (Exception ex)
            {
                failed++;
                Failed("redraw the active skater's panel", ex);
            }
        }

        PlayersManager? manager = PlayersManager.Instance;
        string cpu = manager != null ? PlayerMarkers.Describe(Value(manager.cpuColor.arrowColor)) : "unknown";
        ModLog.Info($"Player markers: the host's players here: {Players()}; this PC's CPU colour {cpu}. Redrawn with the game's own code " +
            $"as the stream started (game state {state}): {boxesShown} controlled skater(s)' off-screen boxes (FillStar), {boxesHidden} other box(es) hidden, " +
            $"{stars} star(s) over skaters, {panels} active-skater panel(s){(failed > 0 ? $"; {failed} failed" : string.Empty)}; " +
            $"{Indicators.Count} skater marker(s) in this match, checked once a second from now on.");
    }

    /// <summary>Once a second after the redraw: every shown off-screen box and visible star in its controlling player's colour.</summary>
    private static void Check(int state, double now)
    {
        if (now >= _nextFind)
        {
            Find(now);
        }

        foreach (PlayerIndicator indicator in Indicators)
        {
            try
            {
                CheckBox(indicator, state);
            }
            catch (Exception ex)
            {
                Failed("check an off-screen box", ex);
            }
        }

        PlayersManager? manager = PlayersManager.Instance;
        if (manager == null)
        {
            return;
        }

        ColorValue cpu = Value(manager.cpuColor.arrowColor);
        foreach (SkaterInitUI ui in SkaterUis)
        {
            try
            {
                CheckStar(ui, cpu, state);
            }
            catch (Exception ex)
            {
                Failed("check a star over a skater", ex);
            }
        }
    }

    private static void CheckBox(PlayerIndicator indicator, int state)
    {
        if (indicator == null || indicator._isDisabled || indicator.skater is not Skater skater || skater == null ||
            indicator.offscreenTarget is not OffscreenTarget box || box == null)
        {
            return;
        }

        bool shown = box.enabled;
        ColorValue colour = shown ? Value(box.targetColor) : default;
        Player? controller = shown ? ControllerOf(skater.Pointer) : null;
        ColorValue? expected = controller != null ? Value(controller.playerStarColor.arrowColor) : null;
        if (shown)
        {
            CheckFaceIcon(box, skater, colour, controller, state);
        }

        MarkerFix fix = PlayerMarkers.CheckOffscreenBox(shown, colour, expected);
        if (!BoxWatch.Confirm(indicator.Pointer, fix != MarkerFix.None, (fix, colour)))
        {
            return; // fine, or not yet seen wrong twice in a row (an event on its way may fix it)
        }

        switch (fix)
        {
            case MarkerFix.Recolour:
                indicator.FillStar();
                if (_boxesFixed++ < MaxFixLogs)
                {
                    ModLog.Info($"Player markers: {LogNames.ObjectName(skater)}'s off-screen box was {PlayerMarkers.Describe(colour)}, not game player " +
                        $"{controller!._PlayerId_k__BackingField}'s {PlayerMarkers.Describe(expected!.Value)} (game state {state}); redrawn with the game's own FillStar." +
                        $"{(_boxesFixed == MaxFixLogs ? " Any more are counted." : string.Empty)}");
                }

                break;
            case MarkerFix.Hide:
                indicator.RemoveStar();
                if (_boxesFixed++ < MaxFixLogs)
                {
                    ModLog.Info($"Player markers: {LogNames.ObjectName(skater)}'s off-screen box was shown ({PlayerMarkers.Describe(colour)}) though none of the host's " +
                        $"players controls that skater (game state {state}); hidden with the game's own RemoveStar." +
                        $"{(_boxesFixed == MaxFixLogs ? " Any more are counted." : string.Empty)}");
                }

                break;
        }
    }

    /// <summary>
    /// A shown off-screen box's face icon (a white box on the friend while the stream sent green). The box's coloured image takes its
    /// player's colour; only the face icon can draw white: the game's <c>OffscreenTarget.UpdateFaceVisual</c> turns it on for a skater
    /// and then sets the face it gets with no check, and a UGUI Image with no sprite is a plain square in its colour. The first shown
    /// box's icon is described once per playback (which case drew the box); an icon on with no sprite, seen twice in a row (the game
    /// turns it on before its face arrives), is asked for again with the game's own <c>UpdateFaceVisual</c>, up to
    /// <see cref="PlayerMarkers.MaxFaceRetries"/> times per skater, then hidden so the box shows the player's colour.
    /// </summary>
    private static void CheckFaceIcon(OffscreenTarget box, Skater skater, ColorValue colour, Player? controller, int state)
    {
        Indicator? indicator = box.indicator;
        UnityEngine.UI.Image? icon = indicator != null ? indicator.indicatorIcon : null;
        if (icon == null)
        {
            return; // the box hasn't been drawn yet (the game takes an indicator from its pool when it first is)
        }

        GameObject iconObject = icon.gameObject;
        bool on = iconObject.activeSelf;
        Sprite? sprite = on ? icon.sprite : null;
        bool hasSprite = sprite != null;
        if (!_faceReadout)
        {
            _faceReadout = true;
            ModLog.Info($"Player markers: the first off-screen box shown here: {LogNames.ObjectName(skater)}'s, {PlayerMarkers.Describe(colour)}" +
                $"{(controller != null ? $" (game player {controller._PlayerId_k__BackingField})" : string.Empty)}; its face icon " +
                $"{(!on ? "off (the box shows its colour)" : hasSprite ? "on with its face" : "on with no face (Unity draws it as a white square)")} (game state {state}).");
        }

        int retries = FaceRetries.GetValueOrDefault(skater.Pointer);
        FaceIconFix fix = PlayerMarkers.CheckFaceIcon(on, hasSprite, retries);
        if (!FaceWatch.Confirm(indicator!.Pointer, fix != FaceIconFix.None, fix))
        {
            return; // fine, or not yet seen twice in a row (its face may be on its way)
        }

        bool logged = _faceFixes++ < MaxFixLogs;
        if (fix == FaceIconFix.Retry)
        {
            FaceRetries[skater.Pointer] = retries + 1;
            box.UpdateFaceVisual();
            if (logged)
            {
                ModLog.Info($"Player markers: {LogNames.ObjectName(skater)}'s off-screen box shows its face icon with no face (Unity draws it white; the box's colour is " +
                    $"{(controller != null ? $"game player {controller._PlayerId_k__BackingField}'s " : string.Empty)}{PlayerMarkers.Describe(colour)}); asked the game for the face " +
                    $"again (OffscreenTarget.UpdateFaceVisual, {retries + 1} of {PlayerMarkers.MaxFaceRetries}).{(_faceFixes == MaxFixLogs ? " Any more are counted." : string.Empty)}");
            }

            return;
        }

        iconObject.SetActive(false);
        HiddenIcons[iconObject.Pointer] = iconObject;
        if (logged)
        {
            ModLog.Info($"Player markers: {LogNames.ObjectName(skater)}'s off-screen box still has no face after asking {PlayerMarkers.MaxFaceRetries} times; its face icon is " +
                "hidden, so the box shows the player's colour (the host's box shows the face).");
        }
    }

    /// <summary>Face icons hidden this playback are put back as the game had them (a match still loaded: the F4 test).</summary>
    private static void ShowHiddenIcons()
    {
        foreach (GameObject icon in HiddenIcons.Values)
        {
            try
            {
                if (icon != null)
                {
                    icon.SetActive(true);
                }
            }
            catch (Exception)
            {
                // Gone with its scene already.
            }
        }

        HiddenIcons.Clear();
    }

    private static void CheckStar(SkaterInitUI ui, ColorValue cpu, int state)
    {
        if (ui == null)
        {
            return;
        }

        SkaterUI? star = ui.m_UI;
        UnityEngine.UI.Graphic? first = star != null && star.gameObject.activeInHierarchy && star.m_StarColorGraphics is { Length: > 0 } graphics ? graphics[0] : null;
        Skater? skater = first != null ? star!.CurrentSkater : null;
        if (first == null || skater == null)
        {
            StarWatch.Confirm(ui.Pointer, false, default); // hidden: nothing to see
            return;
        }

        // SkaterUI.RefreshPlayerControl: the controlling player's colour, else this PC's CPU colour; alpha always 1.
        Player? controller = ControllerOf(skater.Pointer);
        ColorValue expected = controller != null ? Value(controller.playerStarColor.arrowColor) : cpu;
        ColorValue colour = Value(first.color);
        if (!StarWatch.Confirm(ui.Pointer, !PlayerMarkers.SameColour(colour, expected, withAlpha: false), colour))
        {
            return; // fine, or not yet seen wrong twice in a row
        }

        star!.RefreshPlayerControl();
        if (_starsFixed++ < MaxFixLogs)
        {
            ModLog.Info($"Player markers: the star over {LogNames.ObjectName(skater)} was {PlayerMarkers.Describe(colour)}, not " +
                $"{(controller != null ? $"game player {controller._PlayerId_k__BackingField}'s" : "the CPU colour,")} {PlayerMarkers.Describe(expected)} " +
                $"(game state {state}); redrawn with the game's own RefreshPlayerControl.{(_starsFixed == MaxFixLogs ? " Any more are counted." : string.Empty)}");
        }
    }

    /// <summary>The match's skater markers and overhead UIs (skaters come and go with bench swaps), looked up every few seconds.</summary>
    private static void Find(double now)
    {
        _nextFind = now + FindEverySeconds;
        Indicators.Clear();
        Indicators.AddRange(UnityEngine.Object.FindObjectsOfType<PlayerIndicator>());
        SkaterUis.Clear();
        SkaterUis.AddRange(UnityEngine.Object.FindObjectsOfType<SkaterInitUI>());
    }

    /// <summary>"game player 0 red (…) with no skater (Rewired player 0), game player 2 green (…) on skater 1f0c... (Rewired player 2, you)".</summary>
    private static string Players()
    {
        var text = new StringBuilder();
        foreach (int id in ProxyIds)
        {
            if (!Proxies.TryGetValue(id, out Player? proxy) || proxy == null || !RewiredOf.TryGetValue(id, out int rewired))
            {
                continue;
            }

            Skater? skater = proxy.controlledSkater;
            text.Append(text.Length > 0 ? ", " : string.Empty)
                .Append(CultureInfo.InvariantCulture, $"game player {id} {PlayerMarkers.Describe(Value(proxy.playerStarColor.arrowColor))} ")
                .Append(skater != null ? $"on {LogNames.ObjectName(skater)}" : "with no skater")
                .Append(CultureInfo.InvariantCulture, $" (Rewired player {rewired}{(rewired >= 0 && rewired == YourRewired ? ", you" : string.Empty)})");
        }

        return text.Length > 0 ? text.ToString() : "none";
    }

    /// <summary>The stand-in for host player <paramref name="id"/>: a Player on an inactive object (its code never runs).</summary>
    private static Player? Proxy(int id)
    {
        if (Proxies.TryGetValue(id, out Player? existing) && existing != null)
        {
            return existing;
        }

        try
        {
            var holder = new GameObject($"T2TOnline host player {id}");
            holder.SetActive(false);
            Player? proxy = holder.AddComponent(Il2CppType.Of<Player>())?.TryCast<Player>();
            if (proxy == null)
            {
                UnityEngine.Object.Destroy(holder);
                return null;
            }

            proxy._PlayerId_k__BackingField = id;
            Proxies[id] = proxy;
            Coloured.Remove(id);
            if (!ProxyIds.Contains(id))
            {
                ProxyIds.Add(id);
                ProxyIds.Sort();
            }

            return proxy;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Player markers: couldn't make a stand-in for host player {id}: {ModLog.SafeErrorText(ex)}");
            return null;
        }
    }

    private static void Clear()
    {
        foreach (Player proxy in Proxies.Values)
        {
            try
            {
                if (proxy != null)
                {
                    UnityEngine.Object.Destroy(proxy.gameObject);
                }
            }
            catch (Exception)
            {
                // Gone with its scene already.
            }
        }

        Proxies.Clear();
        SkaterOf.Clear();
        SkaterObjectOf.Clear();
        RewiredOf.Clear();
        Coloured.Clear();
        ProxyIds.Clear();
        Indicators.Clear();
        SkaterUis.Clear();
        BoxWatch.Clear();
        StarWatch.Clear();
        FaceWatch.Clear();
        FaceRetries.Clear();
        ShowHiddenIcons();
        _listed = false;
        _redrawPending = false;
    }

    /// <summary>The stand-in controlling the skater at <paramref name="skater"/> (by the pointers the stream set), or null.</summary>
    private static Player? ControllerOf(IntPtr skater)
    {
        if (skater == IntPtr.Zero)
        {
            return null;
        }

        foreach ((int id, IntPtr controlled) in SkaterOf)
        {
            if (controlled == skater && Proxies.TryGetValue(id, out Player? proxy) && proxy != null)
            {
                return proxy;
            }
        }

        return null;
    }

    private static ColorValue Value(Color colour) => new(colour.r, colour.g, colour.b, colour.a);

    /// <summary>The skater object a stand-in controls, kept for the ground markers' per-frame placement (no wrapper per frame).</summary>
    private static void Remember(int id, Skater? skater)
    {
        if (skater != null)
        {
            SkaterObjectOf[id] = skater;
        }
        else
        {
            SkaterObjectOf.Remove(id);
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (_friendFailures++ < MaxFailureLogs)
        {
            ModLog.Warn($"Player markers: couldn't {what}: {ModLog.SafeErrorText(ex)}");
        }
    }

    private static bool Install()
    {
        try
        {
            _harmony ??= new HarmonyLib.Harmony("t2tonline.players");
            Patch(typeof(PlayersManager), nameof(PlayersManager.IsSkaterControlled), nameof(IsSkaterControlledPrefix));
        }
        catch (Exception ex)
        {
            ModLog.Error("Player markers: couldn't patch PlayersManager.IsSkaterControlled; the players' markers won't show while following a match", ex);
            return false;
        }

        try
        {
            Patch(typeof(GroundMarkers), nameof(GroundMarkers.PlaceTeamMarkers), nameof(PlaceTeamMarkersPrefix));
        }
        catch (Exception ex)
        {
            // Costs only the rings of the squads that have them; the other markers still show.
            ModLog.Error("Player markers: couldn't patch SkaterGroundMarkers.PlaceTeamMarkers; a squad's ground markers won't show the players' colours while following a match", ex);
        }

        return true;
    }

    private static void Patch(Type type, string method, string prefix)
    {
        MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
        _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(PlayerProxies), prefix));
    }

    /// <summary>
    /// While playing a stream: which (mirrored) player controls this skater, by the skater pointers the stream set (the
    /// stand-ins' own fields hold the same skaters, which keeps those objects alive, so a pointer can't be reused while
    /// it's here). Never throws into the game.
    /// </summary>
    private static bool IsSkaterControlledPrefix(Skater skater, ref Player player, ref bool __result)
    {
        if (!_active || !_listed)
        {
            return true;
        }

        try
        {
            _answered++;
            Player? controller = ControllerOf(skater?.Pointer ?? IntPtr.Zero);
            player = controller!;
            __result = controller != null;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// While playing a stream: a squad's ground markers (<c>SkaterGroundMarkers</c>, placed every frame) under the skaters
    /// of the host's players on <paramref name="team"/>, in their colours, as the game places them from the team's own
    /// players (<c>Team._players</c>, empty on a friend's PC). A team none of the host's players skates for keeps the
    /// game's own placement (the puck carrier in the CPU colour). Never throws into the game.
    /// </summary>
    private static bool PlaceTeamMarkersPrefix(GroundMarkers __instance, Team team, Skater carrier, int used, ref int __result)
    {
        if (!_active || !_listed || team == null)
        {
            return true;
        }

        try
        {
            int index = used;
            foreach (int id in ProxyIds)
            {
                if (!SkaterObjectOf.TryGetValue(id, out Skater? skater) || skater == null || !Proxies.TryGetValue(id, out Player? proxy) || proxy == null)
                {
                    continue;
                }

                if (skater._team is not Team skaterTeam || skaterTeam.Pointer != team.Pointer)
                {
                    continue;
                }

                __instance.Place(index++, skater, carrier, proxy.playerStarColor.arrowColor);
            }

            if (index == used)
            {
                return true;
            }

            _groundFrames++;
            if (!_groundNoted)
            {
                _groundNoted = true;
                ModLog.Info($"Player markers: this squad's ground markers are placed under the host's players' skaters, in their colours ({Players()}).");
            }

            __result = index;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
#endif
