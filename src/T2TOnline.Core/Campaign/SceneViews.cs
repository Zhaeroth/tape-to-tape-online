using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>A node on the campaign map: its column and its floor (the game's <c>STS.Map.Point</c>), written "x,y".</summary>
/// <param name="X">The column (0 to <see cref="MaxCoordinate"/>), or -1 for <see cref="None"/>.</param>
/// <param name="Y">The floor (0 to <see cref="MaxCoordinate"/>), or -1 for <see cref="None"/>.</param>
public readonly record struct MapPoint(int X, int Y)
{
    /// <summary>The largest column or floor taken from the network (an act has about 16 floors and 7 columns).</summary>
    public const int MaxCoordinate = 63;

    /// <summary>No node: before the first node of an act ("-1,-1", as the game's <c>currentNodePoint</c> has it).</summary>
    public static MapPoint None { get; } = new(-1, -1);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => X == -1 && Y == -1;

    /// <summary>Reads "x,y" (or "-1,-1"). False for anything else, or a coordinate out of range. Never throws.</summary>
    public static bool TryParse(string? text, out MapPoint point)
    {
        point = None;
        if (string.IsNullOrEmpty(text) || text.Length > 8)
        {
            return false;
        }

        int comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma == text.Length - 1 ||
            !int.TryParse(text.AsSpan(0, comma), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(text.AsSpan(comma + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y))
        {
            return false;
        }

        var read = new MapPoint(x, y);
        if (!read.IsNone && (x < 0 || y < 0 || x > MaxCoordinate || y > MaxCoordinate))
        {
            return false;
        }

        point = read;
        return true;
    }

    /// <summary>"x,y".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y}");
}

/// <summary>The nodes an act's map has been through (the game's <c>Map.path</c>), written "x,y;x,y;...".</summary>
public static class MapPath
{
    /// <summary>The most nodes carried: an act has about 16 floors, and 40 points fit one detail's 255 bytes.</summary>
    public const int MaxPoints = 40;

    /// <summary>"x,y;x,y;..." ("" for an empty path).</summary>
    public static string Format(IReadOnlyList<MapPoint> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = new StringBuilder(path.Count * 6);
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                text.Append(';');
            }

            text.Append(path[i].ToString());
        }

        return text.ToString();
    }

    /// <summary>Reads a path. False for a malformed one, more than <see cref="MaxPoints"/> nodes, or a <see cref="MapPoint.None"/> in it. Never throws.</summary>
    public static bool TryParse(string? text, out List<MapPoint> path)
    {
        path = new List<MapPoint>();
        if (string.IsNullOrEmpty(text))
        {
            return text != null;
        }

        string[] parts = text.Split(';');
        if (parts.Length > MaxPoints)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!MapPoint.TryParse(part, out MapPoint point) || point.IsNone)
            {
                path.Clear();
                return false;
            }

            path.Add(point);
        }

        return true;
    }

    /// <summary>True when both paths hold the same nodes in the same order.</summary>
    public static bool Same(IReadOnlyList<MapPoint> a, IReadOnlyList<MapPoint> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count && StartsWith(a, b);
    }

    /// <summary>True when <paramref name="longer"/> begins with every node of <paramref name="prefix"/>, in order.</summary>
    public static bool StartsWith(IReadOnlyList<MapPoint> longer, IReadOnlyList<MapPoint> prefix)
    {
        ArgumentNullException.ThrowIfNull(longer);
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Count > longer.Count)
        {
            return false;
        }

        for (int i = 0; i < prefix.Count; i++)
        {
            if (longer[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Detail names of the scene views (the world map and match previews), beyond the shared ones.</summary>
public static class SceneViewKeys
{
    /// <summary>Text: the run's id.</summary>
    public const string Run = "run";

    /// <summary>Whole: the act.</summary>
    public const string Act = "act";

    /// <summary>Text: a node "x,y" (the map's current node; a preview's node).</summary>
    public const string Node = "node";

    /// <summary>Text: the act's path so far (<see cref="MapPath"/>).</summary>
    public const string Path = "path";

    /// <summary>Text: the map node the cursor is on (absent: not on a node).</summary>
    public const string Selected = "sel";

    /// <summary>Single: the map camera's scroll target (absent: unknown).</summary>
    public const string CameraX = "camX";

    /// <summary>
    /// Flag: whether the node the cursor is on shows its tooltip on the host's screen (its details: the game's
    /// <c>MapNode.m_TooltipContainer</c>, up while the node is selected and the host's "world map node tooltips" setting is
    /// on). Absent: not known (no node, or an older host).
    /// </summary>
    public const string Tooltip = "tip";

    /// <summary>
    /// Text: the relic the cursor is on in the map's relic bar (its asset id: the game's <c>ManualRelicIcon.CurrentRelic</c>),
    /// with <see cref="RelicAt"/>. Absent: the cursor isn't in the relic bar (or an older host).
    /// </summary>
    public const string Relic = "relic";

    /// <summary>Whole: the place in the relic bar of the relic the cursor is on (0 first). Present exactly when <see cref="Relic"/> is.</summary>
    public const string RelicAt = "relicAt";

    /// <summary>
    /// The details that change as the cursor and camera move (several times a second): not worth a log line. The title's
    /// highlighted button (<see cref="TitleViewDetails.ButtonKey"/>) is one too.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Transient = new[] { Selected, CameraX, Tooltip, Relic, RelicAt, TitleViewDetails.ButtonKey };

    /// <summary>
    /// Text: the event node the host's game is entering ("x,y": its camera push into the node and the zamboni's drive, from
    /// the click until the camera has come back; <see cref="NodeEntry"/>). Absent: none under way (or an older host).
    /// </summary>
    public const string Enter = "enter";

    /// <summary>Whole: which entry <see cref="Enter"/> is (1 and up, counted per hosting session). Present exactly when <see cref="Enter"/> is.</summary>
    public const string EnterNumber = "enterNo";

    /// <summary>Whole: the act whose intro the host's game played as it made this map (absent: none).</summary>
    public const string Intro = "intro";

    /// <summary>
    /// Flag: the host's game has started playing <see cref="Intro"/>'s intro (it holds its start until every friend's game
    /// has the map, at most a few seconds). Absent: not yet (or an older host).
    /// </summary>
    public const string IntroStarted = "introGo";

    /// <summary>Whole: a preview's node type (the game's <c>NodeType</c>: Challenge 0, Elite 1, Boss 2).</summary>
    public const string NodeType = "type";

    /// <summary>Text, one per playing slot: the opponent's skater (its asset id, <c>OriginalId</c>), "" for none.</summary>
    public const string OpponentSlot = "opp.";

    /// <summary>Text: the opponent's goalie (its asset id).</summary>
    public const string OpponentGoalie = "oppGoalie";

    /// <summary>Text: the relic the preview shows as a possible reward ("" for none).</summary>
    public const string RewardRelic = "reward.relic";

    /// <summary>Text: the talent the preview shows as a possible reward ("" for none).</summary>
    public const string RewardTalent = "reward.talent";

    /// <summary>Text: the ability the preview shows as a possible reward ("" for none).</summary>
    public const string RewardAbility = "reward.ability";

    /// <summary>
    /// Flag: the host started the match from this preview and its zoom into the node is playing (absent: not yet). A friend
    /// plays the same zoom at once, so it ends before the friend's copy of the match loads over the map.
    /// </summary>
    public const string Zoom = "zoom";
}

/// <summary>
/// Where the host's world map is (the "map" view's details): the run and act, the act's path (whose last node is the
/// current one), and the cursor (the node it's on and the camera's scroll target). Values from the network are checked
/// by <see cref="TryRead"/>.
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Act">The act (1-3).</param>
/// <param name="Path">The act's path so far; the last node is the current one.</param>
/// <param name="Selected">The node the cursor is on, or null (not on a node).</param>
/// <param name="CameraX">The camera's scroll target, or null.</param>
public sealed record MapViewDetails(string RunId, int Act, IReadOnlyList<MapPoint> Path, MapPoint? Selected, float? CameraX)
{
    /// <summary>The longest run id taken (a GUID is 36 characters).</summary>
    public const int MaxRunIdLength = 64;

    /// <summary>The highest act taken (the game has 3).</summary>
    public const int MaxAct = 9;

    /// <summary>The largest camera position taken, either way.</summary>
    public const float MaxCameraX = 100_000f;

    /// <summary>The longest relic id taken.</summary>
    public const int MaxRelicIdLength = 96;

    /// <summary>The last place in the relic bar taken.</summary>
    public const int MaxRelicPlace = 63;

    /// <summary>The current node: the path's last, or <see cref="MapPoint.None"/>.</summary>
    public MapPoint Node => Path.Count > 0 ? Path[^1] : MapPoint.None;

    /// <summary>
    /// The act whose intro (its title card and camera fly-over) the host's game played as it made this map, a new run's or
    /// a new act's (1 to <see cref="MaxAct"/>), or null. A friend's game, which only ever loads the host's map, plays it
    /// too when it loads this one. Absent from older hosts' views.
    /// </summary>
    public int? Intro { get; init; }

    /// <summary>
    /// Whether the host's game has started playing <see cref="Intro"/>'s intro (true), or holds it while friends load the map
    /// (false); null when the view doesn't say (no intro, or an older host). A friend's copy of the intro waits for it.
    /// </summary>
    public bool? IntroStarted { get; init; }

    /// <summary>
    /// The event node the host's game is entering (its camera push and the zamboni's drive into the node, before the event
    /// screen), or null. A friend's game plays the same entry once (<see cref="NodeEntryFollow"/>). Absent from older hosts' views.
    /// </summary>
    public NodeEntry? Entering { get; init; }

    /// <summary>
    /// Whether the node the cursor is on shows its tooltip (its details) on the host's screen; null when the host didn't
    /// say (an older host, or no node). A friend's map shows or hides the same node's tooltip, in its own language.
    /// </summary>
    public bool? TooltipShown { get; init; }

    /// <summary>
    /// The relic the host's cursor is on in the map's relic bar (its asset id), or null when the cursor isn't in the bar. A
    /// friend's map opens its own relic bar (the game's own toggle) and puts its cursor on the same relic, whose details show
    /// in its own language. Absent from older hosts' views.
    /// </summary>
    public string? Relic { get; init; }

    /// <summary>That relic's place in the bar (0 first), for a bar with the relic twice; null exactly when <see cref="Relic"/> is.</summary>
    public int? RelicAt { get; init; }

    /// <summary>The details, for <c>PublishView</c>.</summary>
    public List<KeyValuePair<string, NetValue>> ToValues()
    {
        List<KeyValuePair<string, NetValue>> values = PlaceValues();
        values.Add(new(SceneViewKeys.Node, NetValue.FromString(Node.ToString())));
        if (Selected is MapPoint selected)
        {
            values.Add(new(SceneViewKeys.Selected, NetValue.FromString(selected.ToString())));
        }

        if (CameraX is float camera)
        {
            values.Add(new(SceneViewKeys.CameraX, NetValue.FromFloat(camera)));
        }

        if (Intro is int intro)
        {
            values.Add(new(SceneViewKeys.Intro, NetValue.FromInt(intro)));
        }

        if (IntroStarted is bool started)
        {
            values.Add(new(SceneViewKeys.IntroStarted, NetValue.FromBool(started)));
        }

        if (Entering is { } entering)
        {
            values.Add(new(SceneViewKeys.Enter, NetValue.FromString(entering.Node.ToString())));
            values.Add(new(SceneViewKeys.EnterNumber, NetValue.FromInt(entering.Number)));
        }

        if (TooltipShown is bool tooltip)
        {
            values.Add(new(SceneViewKeys.Tooltip, NetValue.FromBool(tooltip)));
        }

        if (Relic is { } relic && RelicAt is int at)
        {
            values.Add(new(SceneViewKeys.Relic, NetValue.FromString(relic)));
            values.Add(new(SceneViewKeys.RelicAt, NetValue.FromInt(at)));
        }

        return values;
    }

    /// <summary>True when <paramref name="other"/> is the same run, act and path (the cursor may differ).</summary>
    public bool SamePlace(MapViewDetails other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return RunId == other.RunId && Act == other.Act && MapPath.Same(Path, other.Path);
    }

    /// <summary>
    /// Reads a host's map view. False, with why, for missing or malformed details (an unknown run id, an act out of
    /// range, a bad path, a current node that isn't the path's last, a bad cursor). Never throws.
    /// </summary>
    public static bool TryRead(HostView view, out MapViewDetails? details, out string problem)
    {
        ArgumentNullException.ThrowIfNull(view);
        details = null;
        if (!TryReadPlace(view, out string runId, out int act, out List<MapPoint> path, out problem))
        {
            return false;
        }

        if (!MapPoint.TryParse(view.Text(SceneViewKeys.Node), out MapPoint node) || node != (path.Count > 0 ? path[^1] : MapPoint.None))
        {
            problem = $"the current node ({SafeDetail(view, SceneViewKeys.Node)}) isn't the path's last";
            return false;
        }

        MapPoint? selected = null;
        if (view.TryGet(SceneViewKeys.Selected, out _))
        {
            if (!MapPoint.TryParse(view.Text(SceneViewKeys.Selected), out MapPoint point) || point.IsNone)
            {
                problem = $"bad cursor node ({SafeDetail(view, SceneViewKeys.Selected)})";
                return false;
            }

            selected = point;
        }

        float? camera = null;
        if (view.TryGet(SceneViewKeys.CameraX, out NetValue cameraValue))
        {
            if (cameraValue.Kind != NetValueKind.Single32 || !double.IsFinite(cameraValue.Number) || Math.Abs(cameraValue.Number) > MaxCameraX)
            {
                problem = "bad camera position";
                return false;
            }

            camera = (float)cameraValue.Number;
        }

        int? intro = null;
        if (view.TryGet(SceneViewKeys.Intro, out NetValue introValue))
        {
            if (introValue.Kind != NetValueKind.Whole || introValue.WholeValue < 1 || introValue.WholeValue > MaxAct)
            {
                problem = "bad intro act";
                return false;
            }

            intro = (int)introValue.WholeValue;
        }

        bool? introStarted = null;
        if (view.TryGet(SceneViewKeys.IntroStarted, out NetValue startedValue))
        {
            if (startedValue.Kind != NetValueKind.Flag)
            {
                problem = "bad intro start flag";
                return false;
            }

            introStarted = startedValue.WholeValue != 0;
        }

        if (!TryReadEntry(view, out NodeEntry? entering, out problem))
        {
            return false;
        }

        bool? tooltip = null;
        if (view.TryGet(SceneViewKeys.Tooltip, out NetValue tooltipValue))
        {
            if (tooltipValue.Kind != NetValueKind.Flag)
            {
                problem = "bad tooltip flag";
                return false;
            }

            tooltip = tooltipValue.WholeValue != 0;
        }

        if (!TryReadRelic(view, out string? relic, out int? relicAt, out problem))
        {
            return false;
        }

        details = new MapViewDetails(runId, act, path, selected, camera) { Intro = intro, IntroStarted = introStarted, Entering = entering, TooltipShown = tooltip, Relic = relic, RelicAt = relicAt };
        return true;
    }

    /// <summary>
    /// Reads the relic bar's cursor: <see cref="SceneViewKeys.Relic"/> and <see cref="SceneViewKeys.RelicAt"/> both or neither,
    /// an id of 1 to <see cref="MaxRelicIdLength"/> characters with no control character, a place from 0 to
    /// <see cref="MaxRelicPlace"/>. False, with why, otherwise. Never throws.
    /// </summary>
    private static bool TryReadRelic(HostView view, out string? relic, out int? at, out string problem)
    {
        relic = null;
        at = null;
        problem = string.Empty;
        bool hasRelic = view.TryGet(SceneViewKeys.Relic, out NetValue relicValue);
        bool hasAt = view.TryGet(SceneViewKeys.RelicAt, out NetValue atValue);
        if (!hasRelic && !hasAt)
        {
            return true;
        }

        string id = relicValue.Kind == NetValueKind.Text ? relicValue.Text ?? string.Empty : string.Empty;
        bool good = hasRelic && hasAt && id.Length is > 0 and <= MaxRelicIdLength && !id.Any(char.IsControl) &&
            atValue.Kind == NetValueKind.Whole && atValue.WholeValue is >= 0 and <= MaxRelicPlace;
        if (!good)
        {
            problem = $"bad relic bar cursor ({SafeDetail(view, SceneViewKeys.Relic)} at {SafeDetail(view, SceneViewKeys.RelicAt)})";
            return false;
        }

        relic = id;
        at = (int)atValue.WholeValue;
        return true;
    }

    /// <summary>
    /// Reads the node entry: <see cref="SceneViewKeys.Enter"/> and <see cref="SceneViewKeys.EnterNumber"/> both or neither, a
    /// node on the map (not <see cref="MapPoint.None"/>) and a number from 1. False, with why, otherwise. Never throws.
    /// </summary>
    private static bool TryReadEntry(HostView view, out NodeEntry? entering, out string problem)
    {
        entering = null;
        problem = string.Empty;
        bool hasNode = view.TryGet(SceneViewKeys.Enter, out NetValue nodeValue);
        bool hasNumber = view.TryGet(SceneViewKeys.EnterNumber, out NetValue numberValue);
        if (!hasNode && !hasNumber)
        {
            return true;
        }

        if (!hasNode || !hasNumber || nodeValue.Kind != NetValueKind.Text || !MapPoint.TryParse(nodeValue.Text, out MapPoint node) || node.IsNone ||
            numberValue.Kind != NetValueKind.Whole || numberValue.WholeValue < 1 || numberValue.WholeValue > NodeEntry.MaxNumber)
        {
            problem = $"bad node entry ({SafeDetail(view, SceneViewKeys.Enter)} #{SafeDetail(view, SceneViewKeys.EnterNumber)})";
            return false;
        }

        entering = new NodeEntry(node, (int)numberValue.WholeValue);
        return true;
    }

    /// <summary>The run, act and path (no current node: a preview's "node" is the previewed one).</summary>
    internal List<KeyValuePair<string, NetValue>> PlaceValues() => new()
    {
        new(SceneViewKeys.Run, NetValue.FromString(RunId)),
        new(SceneViewKeys.Act, NetValue.FromInt(Act)),
        new(SceneViewKeys.Path, NetValue.FromString(MapPath.Format(Path))),
    };

    /// <summary>Reads the run, act and path of a map or preview view. False, with why, when one is missing or malformed.</summary>
    internal static bool TryReadPlace(HostView view, out string runId, out int act, out List<MapPoint> path, out string problem)
    {
        runId = view.Text(SceneViewKeys.Run);
        long actValue = view.Whole(SceneViewKeys.Act, -1);
        act = (int)Math.Clamp(actValue, -1, MaxAct + 1);
        path = new List<MapPoint>();
        if (!IsAssetId(runId, MaxRunIdLength))
        {
            problem = "no run id";
            return false;
        }

        if (actValue < 1 || actValue > MaxAct)
        {
            problem = $"bad act ({actValue})";
            return false;
        }

        if (!view.TryGet(SceneViewKeys.Path, out NetValue pathValue) || pathValue.Kind != NetValueKind.Text || !MapPath.TryParse(pathValue.Text, out path))
        {
            problem = "bad map path";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// An id the game uses (a run's GUID, an asset's id): 1 to <paramref name="maxLength"/> characters, none of them a
    /// control character or the '?' that <c>SafeText</c> puts in place of unsafe ones.
    /// </summary>
    internal static bool IsAssetId(string text, int maxLength)
    {
        if (text.Length == 0 || text.Length > maxLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c < ' ' || c == '?' || c == '\u007F')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A detail as text for a problem message: a host's text by <see cref="HostText.KeyForLog(string?, int)"/> (a point or an
    /// id as it is, anything else as its length), any other value as it is; cut short.
    /// </summary>
    internal static string SafeDetail(HostView view, string key) =>
        view.TryGet(key, out NetValue value) ? SafeText.Clean(HostView.ValueForLog(value), 24) : "none";
}

/// <summary>
/// A match node's preview as the host's game shows it (the "preview" view's details): where the map is, the node, the
/// opponent's five playing skaters and goalie after the game's substitutes, and the possible rewards the preview shows
/// (rolled when it opens). A friend's game opens the same preview with these instead of rolling its own.
/// </summary>
/// <param name="Map">The run, act and path (no cursor).</param>
/// <param name="Target">The previewed node.</param>
/// <param name="NodeType">The node's type: Challenge 0, Elite 1, Boss 2.</param>
/// <param name="Opponent">The opponent's skater per playing slot (<see cref="Slots"/> of them; "" for an empty slot).</param>
/// <param name="Goalie">The opponent's goalie ("" for none).</param>
/// <param name="RewardRelic">The relic shown as a possible reward ("" for none).</param>
/// <param name="RewardTalent">The talent shown as a possible reward ("" for none).</param>
/// <param name="RewardAbility">The ability shown as a possible reward ("" for none).</param>
/// <remarks><see cref="Zooming"/>: the host started the match from this preview (its zoom into the node is playing).</remarks>
public sealed record PreviewViewDetails(
    MapViewDetails Map,
    MapPoint Target,
    int NodeType,
    IReadOnlyList<string> Opponent,
    string Goalie,
    string RewardRelic,
    string RewardTalent,
    string RewardAbility)
{
    /// <summary>Playing slots per team (four skaters and the right defenceman, as the game's lineups have them).</summary>
    public const int Slots = 5;

    /// <summary>The longest asset id taken.</summary>
    public const int MaxAssetIdLength = 128;

    /// <summary>The match node types: Challenge, Elite, Boss.</summary>
    public const int MaxMatchNodeType = 2;

    /// <summary>The host started the match from this preview: its zoom into the node is playing.</summary>
    public bool Zooming { get; init; }

    /// <summary>The details, for <c>PublishView</c>.</summary>
    public List<KeyValuePair<string, NetValue>> ToValues()
    {
        List<KeyValuePair<string, NetValue>> values = Map.PlaceValues();
        values.Add(new(SceneViewKeys.Node, NetValue.FromString(Target.ToString())));
        values.Add(new(SceneViewKeys.NodeType, NetValue.FromInt(NodeType)));
        for (int i = 0; i < Slots; i++)
        {
            values.Add(new(SceneViewKeys.OpponentSlot + i.ToString(CultureInfo.InvariantCulture), NetValue.FromString(i < Opponent.Count ? Opponent[i] : string.Empty)));
        }

        values.Add(new(SceneViewKeys.OpponentGoalie, NetValue.FromString(Goalie)));
        values.Add(new(SceneViewKeys.RewardRelic, NetValue.FromString(RewardRelic)));
        values.Add(new(SceneViewKeys.RewardTalent, NetValue.FromString(RewardTalent)));
        values.Add(new(SceneViewKeys.RewardAbility, NetValue.FromString(RewardAbility)));
        if (Zooming)
        {
            values.Add(new(SceneViewKeys.Zoom, NetValue.FromBool(true)));
        }

        return values;
    }

    /// <summary>Reads a host's preview view. False, with why, for missing or malformed details. Never throws.</summary>
    public static bool TryRead(HostView view, out PreviewViewDetails? details, out string problem)
    {
        ArgumentNullException.ThrowIfNull(view);
        details = null;
        if (!MapViewDetails.TryReadPlace(view, out string runId, out int act, out List<MapPoint> path, out problem))
        {
            return false;
        }

        if (!MapPoint.TryParse(view.Text(SceneViewKeys.Node), out MapPoint target) || target.IsNone)
        {
            problem = $"bad node ({MapViewDetails.SafeDetail(view, SceneViewKeys.Node)})";
            return false;
        }

        long type = view.Whole(SceneViewKeys.NodeType, -1);
        if (type < 0 || type > MaxMatchNodeType)
        {
            problem = $"not a match node (type {type})";
            return false;
        }

        var opponent = new List<string>(Slots);
        for (int i = 0; i < Slots; i++)
        {
            if (!TryReadId(view, SceneViewKeys.OpponentSlot + i.ToString(CultureInfo.InvariantCulture), out string id, out problem))
            {
                return false;
            }

            opponent.Add(id);
        }

        if (!TryReadId(view, SceneViewKeys.OpponentGoalie, out string goalie, out problem) ||
            !TryReadId(view, SceneViewKeys.RewardRelic, out string relic, out problem) ||
            !TryReadId(view, SceneViewKeys.RewardTalent, out string talent, out problem) ||
            !TryReadId(view, SceneViewKeys.RewardAbility, out string ability, out problem))
        {
            return false;
        }

        bool zooming = view.TryGet(SceneViewKeys.Zoom, out NetValue zoom) && zoom.Kind == NetValueKind.Flag && zoom.WholeValue != 0;
        details = new PreviewViewDetails(new MapViewDetails(runId, act, path, null, null), target, (int)type, opponent, goalie, relic, talent, ability)
        {
            Zooming = zooming,
        };
        return true;
    }

    /// <summary>An optional asset id detail: present as text, and either empty or a valid id.</summary>
    private static bool TryReadId(HostView view, string key, out string id, out string problem)
    {
        id = string.Empty;
        problem = string.Empty;
        if (!view.TryGet(key, out NetValue value) || value.Kind != NetValueKind.Text)
        {
            problem = $"no {key}";
            return false;
        }

        id = value.Text ?? string.Empty;
        if (id.Length > 0 && !MapViewDetails.IsAssetId(id, MaxAssetIdLength))
        {
            problem = $"bad {key}";
            id = string.Empty;
            return false;
        }

        return true;
    }
}

/// <summary>What a friend's game does to show the host's world map (<see cref="MapFollow.Decide"/>).</summary>
public enum MapMove
{
    /// <summary>It already shows the host's map at the same node: only the cursor moves.</summary>
    Stay,

    /// <summary>It shows the host's map, and the host has moved on along the path: bring the run in and move the current node in place.</summary>
    Advance,

    /// <summary>Anything else: bring the run in and load the world map from it.</summary>
    Reload,
}

/// <summary>Where a friend's game is on a world map it holds: the run, act and path it shows.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Act">The act.</param>
/// <param name="Path">The act's path so far.</param>
public sealed record MapPlace(string RunId, int Act, IReadOnlyList<MapPoint> Path);

/// <summary>Game-independent decisions of a friend's game following the host through the title screen and the world map.</summary>
public static class MapFollow
{
    /// <summary>
    /// How to get from <paramref name="here"/> (the map the friend's game shows with the host's run; null when it shows
    /// none: another scene, a map still loading, or its own run) to the host's <paramref name="host"/> map, and why.
    /// </summary>
    public static MapMove Decide(MapPlace? here, MapViewDetails host, out string why)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (here == null)
        {
            why = "not on the host's world map";
            return MapMove.Reload;
        }

        if (here.RunId != host.RunId)
        {
            why = "another run";
            return MapMove.Reload;
        }

        if (here.Act != host.Act)
        {
            why = $"act {here.Act} here, {host.Act} on the host";
            return MapMove.Reload;
        }

        if (MapPath.Same(here.Path, host.Path))
        {
            why = "same node";
            return MapMove.Stay;
        }

        if (MapPath.StartsWith(host.Path, here.Path))
        {
            why = $"{host.Path.Count - here.Path.Count} node(s) further along";
            return MapMove.Advance;
        }

        why = "the path differs";
        return MapMove.Reload;
    }

    /// <summary>
    /// True when a friend's game, sent back to the title by the host, has to leave the campaign (put its own run back
    /// and load its own title screen): it holds the host's run, or shows a world map (which only a campaign has).
    /// </summary>
    public static bool LeaveForTitle(bool holdingHostRun, string scene) => holdingHostRun || scene == "world_map";
}

/// <summary>
/// Lets a changing value out at most once per interval: a cursor republished on change, but not every frame. The last
/// change is never lost: the caller compares what it has with what went out and offers again on a later frame.
/// </summary>
public sealed class RepublishGate
{
    private readonly long _intervalMicros;
    private long _lastMicros;
    private bool _passed;

    /// <summary>A gate letting a change through once per <paramref name="intervalMicros"/>.</summary>
    public RepublishGate(long intervalMicros)
    {
        if (intervalMicros < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMicros), "The interval can't be negative.");
        }

        _intervalMicros = intervalMicros;
    }

    /// <summary>True when a change may go out at <paramref name="nowMicros"/> (and counts it as gone out).</summary>
    public bool TryPass(long nowMicros)
    {
        if (_passed && nowMicros - _lastMicros < _intervalMicros)
        {
            return false;
        }

        _passed = true;
        _lastMicros = nowMicros;
        return true;
    }

    /// <summary>Forgets the last change: the next one goes out at once.</summary>
    public void Reset() => _passed = false;
}
