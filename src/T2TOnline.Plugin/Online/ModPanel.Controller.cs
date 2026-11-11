#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Online.Panel;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

// The panel's Controller tab: a check of this PC's pad before a session (what it is, what a host reads it as, which of a
// standard pad's 20 elements its gamepad template has, each lit while active and marked once read, with the game actions
// it gives under the player's own maps), and while hosting with friends, what each friend's controller has given the game.
// The pad is read every frame the tab is shown (in the update, never while drawing); the friends once a second. The check
// ends, logged in one line, when the tab or the panel closes. Apart from the rest of the panel (ModPanel.cs).
internal sealed partial class ModPanel
{
    /// <summary>How often friends' controllers are read while the tab shows them.</summary>
    private const long FriendsEveryMs = 1000;

    /// <summary>How long an action a friend's controller just gave stays under "Just now".</summary>
    private const long JustNowMs = 10_000;

    /// <summary>Under the tab's heading: pads still play while the panel is open.</summary>
    private const string PadsPlayNote = "Pads still play while this panel is open: pressing buttons here also presses them in the game. Do the check at the title screen.";

    /// <summary>The same, while the mod holds the pad for a session: what's pressed goes to the host's game.</summary>
    private const string HeldPlayNote = "Your pad plays while this panel is open: pressing buttons here also presses them in the host's game (or the F8 test).";

    /// <summary>The friends view's explanation.</summary>
    private const string FriendsIntro = "The game actions each friend's controller has given your game this session, and those its maps bind that haven't come " +
        "yet (read every second).";

    /// <summary>The hint under the grid before anything is pressed or pointed at.</summary>
    private const string DetailHint = "Point at a row, or press something, to see every game action it gives.";

    // Values shown beside the bars, made once: a trigger's 0.00 to 1.00 and a stick's -1.00 to +1.00, in steps of 0.05.
    private static readonly string[] TriggerTexts = Steps(0, 20, "0.00");
    private static readonly string[] StickTexts = Steps(-20, 20, "+0.00;-0.00;0.00");

    private readonly PadCheckReader _padCheck = new();
    private readonly Dictionary<ulong, FriendSeen> _friendSeen = new();
    private readonly string? _padCheckOff;
    private bool _checkRunning;
    private bool _friendsView;
    private bool _friendsViewChosen;
    private long _friendsAt = long.MinValue;
    private IReadOnlyList<FriendRow> _friendRows = Array.Empty<FriendRow>();
    private int _friendsShown = -1;
    private string _friendsButton = string.Empty;
    private InputKind? _playsWith;
    private long _friendsShownAt = long.MinValue; // when the friends view was first shown since the tab opened (its log line at the end)

    /// <summary>
    /// Each frame while the panel is open on the Controller tab: reads this PC's pad (starting the check when the tab
    /// opens), and friends' controllers once a second while hosting. Off on a game build the mod hasn't tested.
    /// </summary>
    private void UpdateController(long now)
    {
        if (_tab != Tab.Controller || _padCheckOff != null)
        {
            return;
        }

        _checkRunning = true;
        if (_friendsAt == long.MinValue || now - _friendsAt >= FriendsEveryMs)
        {
            _friendsAt = now;
            _playsWith = Read<InputKind?>("what this session plays with", () => _online.PlaysWith, null);
            RefreshFriends(now);
        }

        _padCheck.Update(now, _playsWith);
    }

    /// <summary>
    /// Ends the check when the Controller tab or the panel closes (<paramref name="ending"/> says which): its line goes in
    /// the log. Nothing happens while it isn't running. Never throws.
    /// </summary>
    private void EndController(string ending)
    {
        if (!_checkRunning)
        {
            return;
        }

        _checkRunning = false;
        try
        {
            _padCheck.End(ending, Environment.TickCount64);
            LogFriendsView(ending);
            _friendSeen.Clear();
            _friendRows = Array.Empty<FriendRow>();
            _friendsShown = -1;
            _friendsAt = long.MinValue;
            _friendsViewChosen = false;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Panel: ending the controller check failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// The friends view's log line when the tab or panel closes, one per friend, if it was shown:
    /// what the host watched there is in the log too. Never throws.
    /// </summary>
    private void LogFriendsView(string ending)
    {
        if (_friendsShownAt == long.MinValue)
        {
            return;
        }

        long seconds = Math.Max(0, (Environment.TickCount64 - _friendsShownAt) / 1000);
        _friendsShownAt = long.MinValue;
        IReadOnlyList<FriendController>? friends = Read<IReadOnlyList<FriendController>?>("friends' controllers", _online.FriendControllers, null);
        foreach (FriendController friend in friends ?? Array.Empty<FriendController>())
        {
            var coverage = ActionCoverage.Of(friend.Given, friend.Bound);
            ModLog.Info($"Panel: Controller check (friends, {ending}, {seconds} s): {ModLog.Scrub.Tags.Player(friend.SteamId)}'s {friend.Kind}: gave {coverage.Bound - coverage.NotYet.Count} of the " +
                $"{coverage.Bound} actions its maps bind ({friend.Given.Count} in all); not yet: {ActionCoverage.Names(coverage.NotYet, 300)}.");
        }
    }

    /// <summary>Hosting: each friend's controller as a row (what it gave, what hasn't come, what came in the last seconds).</summary>
    private void RefreshFriends(long now)
    {
        IReadOnlyList<FriendController>? friends = Read<IReadOnlyList<FriendController>?>("friends' controllers", _online.FriendControllers, null);
        if (friends == null || friends.Count == 0)
        {
            _friendRows = Array.Empty<FriendRow>();
            _friendSeen.Clear();
            _friendsView = false;
            return;
        }

        var rows = new List<FriendRow>(friends.Count);
        foreach (FriendController friend in friends)
        {
            if (!_friendSeen.TryGetValue(friend.SteamId, out FriendSeen? seen))
            {
                seen = new FriendSeen(friend.Given);
                _friendSeen[friend.SteamId] = seen;
            }

            seen.Note(friend.Given, now);
            var coverage = ActionCoverage.Of(friend.Given, friend.Bound);
            string recent = seen.Recent(now);
            rows.Add(new FriendRow(
                friend.Name,
                $"{friend.Kind} · {coverage.Line(friend.GameActions)}",
                recent.Length > 0 ? $"Just now: {recent}" : string.Empty,
                coverage.NotYet.Count == 0 ? "Not yet: none, every action its maps bind has come." : $"Not yet: {ActionCoverage.Names(coverage.NotYet, 190)}",
                coverage.NotYet.Count == 0,
                $"Given: {ActionCoverage.Names(friend.Given, 100)}"));
        }

        _friendRows = rows;
        if (rows.Count != _friendsShown)
        {
            _friendsShown = rows.Count;
            _friendsButton = $"Friends ({rows.Count})";
        }

        if (!_friendsViewChosen)
        {
            _friendsView = true; // hosting with friends: what they give is what the host wants to watch
        }
    }

    /// <summary>Reset: this PC's pad's check starts again (the one before is logged when anything was read).</summary>
    private void ResetCheck()
    {
        ModLog.Info("Panel: controller check reset.");
        _padCheck.Reset(Environment.TickCount64);
        Show("The controller check starts again.", PanelDraw.Muted);
    }

    /// <summary>
    /// The Controller tab: this PC's pad's check, or while hosting with friends (a switch on the heading's row) what each
    /// friend's controller has given the game.
    /// </summary>
    private void DrawController(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        float y = body.y;
        bool friends = _friendRows.Count > 0;
        bool friendsView = friends && _friendsView;

        // The fixed buttons first (Reset's control id never moves), then the view switch while hosting with friends.
        if (d.Button(new Rect(x + w - (7 * u), y, 7 * u, 1.8f * u), "Reset", _padCheckOff == null && !friendsView && _padCheck.HasPad))
        {
            Queue("Resetting the controller check", ResetCheck);
        }

        if (friends)
        {
            if (d.Button(new Rect(x + w - (24.2f * u), y, 8 * u, 1.8f * u), "Your pad", true, primary: !friendsView) && friendsView)
            {
                _friendsView = false;
                _friendsViewChosen = true;
            }

            if (d.Button(new Rect(x + w - (15.8f * u), y, 8.4f * u, 1.8f * u), _friendsButton, true, primary: friendsView) && !friendsView)
            {
                _friendsView = true;
                _friendsViewChosen = true;
            }
        }

        Heading("Controller check", x, ref y, w - (friends ? 25 * u : 8 * u));
        DrawOwnMenuRow(x, ref y, w); // in a friend's game: change your controls (ModPanel.OwnMenu.cs)
        float bottom = body.y + body.height;
        if (_padCheckOff != null)
        {
            Paragraph(_padCheckOff, x, ref y, w, PanelDraw.Warn);
            return;
        }

        if (friendsView)
        {
            if (_friendsShownAt == long.MinValue)
            {
                _friendsShownAt = Environment.TickCount64;
            }

            DrawFriends(x, y, w, bottom);
            return;
        }

        DrawPadCheck(x, y, w, bottom);
    }

    /// <summary>
    /// This PC's pad: its name and what it is, its template's coverage and where the actions come from, the 20 elements in
    /// two columns (lit while active, marked once read; a stick's two halves each way), the full actions of the element
    /// pointed at or pressed last, and the verdict at the bottom.
    /// </summary>
    private void DrawPadCheck(float x, float y, float w, float bottom)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        PadCheckReader check = _padCheck;
        if (check.Problem.Length > 0)
        {
            Paragraph(check.Problem, x, ref y, w, PanelDraw.Bad);
        }

        PadCheck? state = check.Check;
        if (state == null)
        {
            Paragraph(check.NoPadText.Length > 0 ? check.NoPadText : "Looking for your gamepad...", x, ref y, w, PanelDraw.Text);
            if (check.OthersLine.Length > 0)
            {
                y += 0.4f * u;
                Paragraph(check.OthersLine, x, ref y, w, PanelDraw.Warn);
            }

            y += 0.6f * u;
            Paragraph(PadsPlayNote, x, ref y, w, PanelDraw.Muted);
            return;
        }

        // What the pad is.
        d.Label(new Rect(x, y, w - (16 * u), 1.6f * u), check.NameLine, d.Bold, PanelDraw.Text);
        d.Label(new Rect(x + w - (16 * u), y, 16 * u, 1.6f * u), check.CountLine, d.SmallRight, PanelDraw.Muted);
        y += 1.6f * u;
        d.Label(new Rect(x, y, w, 1.3f * u), check.IdentityLine, d.Small, PanelDraw.Muted);
        y += 1.3f * u;
        d.Label(new Rect(x, y, w, 1.3f * u), check.CoverageLine, d.Small, check.Lacks ? PanelDraw.Bad : PanelDraw.Text);
        y += 1.4f * u;
        if (check.OnlineNote.Length > 0)
        {
            Paragraph(check.OnlineNote, x, ref y, w, PanelDraw.Warn);
        }

        if (check.OthersLine.Length > 0)
        {
            Paragraph(check.OthersLine, x, ref y, w, PanelDraw.Muted);
        }

        Paragraph(check.Held ? HeldPlayNote : PadsPlayNote, x, ref y, w, PanelDraw.Warn);
        y += 0.4f * u;

        // The verdict at the bottom (always on screen), the line naming one element's actions above it, the grid between.
        PadCheckVerdict verdict = check.Verdict;
        string verdictText = verdict.Text ?? string.Empty;
        float verdictHeight = d.Height(verdictText, d.Wrap, w - (1.2f * u)) + (0.6f * u);
        float verdictY = bottom - verdictHeight;
        float detailY = verdictY - (1.6f * u);
        int hovered = DrawGrid(state, check.Actions, x, y, w, detailY - (0.2f * u));
        int shown = hovered >= 0 ? hovered : state.LastActive;
        d.Label(new Rect(x, detailY, w, 1.4f * u), shown >= 0 ? check.DetailOf(shown) : DetailHint, d.Small, shown >= 0 ? PanelDraw.Text : PanelDraw.Dim);
        d.Fill(new Rect(x, verdictY, w, verdictHeight), PanelDraw.Selected);
        d.Fill(new Rect(x, verdictY, Math.Max(2f, MathF.Round(u * 0.25f)), verdictHeight), VerdictColour(verdict.Outcome));
        d.Label(new Rect(x + (0.6f * u), verdictY + (0.3f * u), w - (1.2f * u), verdictHeight - (0.6f * u)), verdictText, d.Wrap, VerdictColour(verdict.Outcome));
    }

    /// <summary>
    /// The 20 elements in two columns of ten, from <paramref name="top"/> while rows fit above <paramref name="bottom"/>.
    /// Returns the element the mouse is over, or -1.
    /// </summary>
    private int DrawGrid(PadCheck state, IReadOnlyList<string> actions, float x, float top, float w, float bottom)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        const int Rows = PadCheck.ElementCount / 2;
        float gap = 0.8f * u;
        float column = (w - gap) / 2;
        float rowHeight = 1.35f * u;
        float rowStep = 1.4f * u;
        int hovered = -1;
        IReadOnlyList<PadCheckElement> elements = PadCheck.Elements;
        for (int i = 0; i < PadCheck.ElementCount; i++)
        {
            int row = i % Rows;
            float cellX = x + (i / Rows * (column + gap));
            float cellY = top + (row * rowStep);
            if (cellY + rowHeight > bottom)
            {
                continue;
            }

            var cell = new Rect(cellX, cellY, column, rowHeight);
            bool active = state.IsActive(i);
            bool missing = state.IsMissing(i);
            if (active)
            {
                d.Fill(cell, PanelDraw.Selected);
            }
            else if (row % 2 == 0)
            {
                d.Fill(cell, PanelDraw.RowShade);
            }

            if (d.Hovered(cell))
            {
                hovered = i;
            }

            // The marker: read (accent), not yet, or lacking (red); a stick's two halves are its two ways.
            PadCheckElement element = elements[i];
            float marker = 0.7f * u;
            var mark = new Rect(cellX + (0.4f * u), cellY + ((rowHeight - marker) / 2), marker, marker);
            if (missing)
            {
                d.Fill(mark, PanelDraw.Bad);
            }
            else if (element.TwoWay && !state.IsSeen(i))
            {
                d.Marker(new Rect(mark.x, mark.y, marker / 2, marker), state.SeenNegative(i));
                d.Marker(new Rect(mark.x + (marker / 2), mark.y, marker / 2, marker), state.SeenPositive(i));
            }
            else
            {
                d.Marker(mark, state.IsSeen(i));
            }

            float nameX = cellX + (1.5f * u);
            d.Label(new Rect(nameX, cellY, 5.6f * u, rowHeight), element.Name, d.Body, missing ? PanelDraw.Dim : PanelDraw.Text);
            float valueX = nameX + (5.8f * u);
            DrawValue(state, element, i, new Rect(valueX, cellY, 4.6f * u, rowHeight), missing, active);
            float actionsX = valueX + (4.9f * u);
            d.Label(new Rect(actionsX, cellY, cellX + column - actionsX - (0.3f * u), rowHeight), actions[i], d.Small, active ? PanelDraw.Text : PanelDraw.Muted);
        }

        return hovered;
    }

    /// <summary>An element's value: "held" for a button, a bar and its value for an axis (a stick's from the middle), "missing" when the template lacks it.</summary>
    private void DrawValue(PadCheck state, PadCheckElement element, int index, Rect area, bool missing, bool active)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        if (missing)
        {
            d.Label(area, "missing", d.Small, PanelDraw.Bad);
            return;
        }

        if (!element.IsAxis)
        {
            if (active)
            {
                d.Label(area, "held", d.Small, PanelDraw.Good);
            }

            return;
        }

        float value = state.Value(index);
        float barWidth = 2.3f * u;
        float barHeight = 0.45f * u;
        var bar = new Rect(area.x, area.y + ((area.height - barHeight) / 2), barWidth, barHeight);
        d.Fill(bar, PanelDraw.Divider);
        if (element.TwoWay)
        {
            float middle = bar.x + (barWidth / 2);
            float length = Math.Abs(value) * barWidth / 2;
            d.Fill(new Rect(value < 0 ? middle - length : middle, bar.y, length, barHeight), active ? PanelDraw.Accent : PanelDraw.Muted);
            d.Fill(new Rect(middle - 0.5f, bar.y - (0.1f * u), 1f, barHeight + (0.2f * u)), PanelDraw.Muted);
        }
        else
        {
            // A trigger: its pull, the furthest it went (red short of the game's 0.50, green past it), and the 0.50 mark.
            d.Fill(new Rect(bar.x, bar.y, Math.Clamp(value, 0f, 1f) * barWidth, barHeight), active ? PanelDraw.Accent : PanelDraw.Muted);
            float peak = state.Peak(index);
            if (peak >= TriggerCheck.MovedFloor)
            {
                d.Fill(new Rect(bar.x + (Math.Clamp(peak, 0f, 1f) * barWidth) - 1f, bar.y - (0.1f * u), 2f, barHeight + (0.2f * u)),
                    peak >= PadCheck.SeenThreshold ? PanelDraw.Good : PanelDraw.Bad);
            }

            d.Fill(new Rect(bar.x + (PadCheck.SeenThreshold * barWidth) - 0.5f, bar.y - (0.15f * u), 1f, barHeight + (0.3f * u)), PanelDraw.Text);
        }

        string text = element.TwoWay ? StickTexts[Math.Clamp((int)MathF.Round(value * 20) + 20, 0, StickTexts.Length - 1)]
            : TriggerTexts[Math.Clamp((int)MathF.Round(value * 20), 0, TriggerTexts.Length - 1)];
        d.Label(new Rect(bar.x + barWidth + (0.2f * u), area.y, area.width - barWidth - (0.2f * u), area.height), text, d.Small, active ? PanelDraw.Text : PanelDraw.Muted);
    }

    /// <summary>
    /// Hosting with friends: each friend's controller (its name with what came in the last seconds; what it is and how many
    /// of the actions its maps bind came; what hasn't come yet; what came), while they fit.
    /// </summary>
    private void DrawFriends(float x, float y, float w, float bottom)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        Paragraph(FriendsIntro, x, ref y, w, PanelDraw.Muted);
        y += 0.4f * u;
        float textX = x + (0.4f * u);
        float textW = w - (0.8f * u);
        IReadOnlyList<FriendRow> rows = _friendRows;
        for (int i = 0; i < rows.Count; i++)
        {
            FriendRow friend = rows[i];
            float notYet = d.Height(friend.NotYet, d.Wrap, textW);
            float height = (1.6f * u) + (1.3f * u) + notYet + (1.3f * u) + (0.2f * u);
            if (y + height > bottom)
            {
                Paragraph("More friends than fit here: the log lists what each one's controller gave when they leave.", x, ref y, w, PanelDraw.Dim);
                return;
            }

            d.Fill(new Rect(x, y, w, height), PanelDraw.RowShade);
            float line = y + (0.1f * u);
            d.Label(new Rect(textX, line, textW * 0.4f, 1.6f * u), friend.Name, d.Bold, PanelDraw.Text);
            d.Label(new Rect(textX + (textW * 0.4f), line, textW * 0.6f, 1.6f * u), friend.JustNow, d.SmallRight, PanelDraw.Good);
            line += 1.6f * u;
            d.Label(new Rect(textX, line, textW, 1.3f * u), friend.Line, d.Small, PanelDraw.Muted);
            line += 1.3f * u;
            d.Label(new Rect(textX, line, textW, notYet), friend.NotYet, d.Wrap, friend.AllCame ? PanelDraw.Good : PanelDraw.Warn);
            line += notYet;
            d.Label(new Rect(textX, line, textW, 1.3f * u), friend.Given, d.Small, PanelDraw.Text);
            y += height + (0.4f * u);
        }
    }

    /// <summary>A verdict's colour.</summary>
    private static Color VerdictColour(PadCheckOutcome outcome) => outcome switch
    {
        PadCheckOutcome.Complete => PanelDraw.Good,
        PadCheckOutcome.Lacking => PanelDraw.Bad,
        PadCheckOutcome.Partial => PanelDraw.Text,
        _ => PanelDraw.Muted,
    };

    /// <summary>The texts of the values <paramref name="from"/>/20 to <paramref name="to"/>/20, in <paramref name="format"/>.</summary>
    private static string[] Steps(int from, int to, string format)
    {
        var texts = new string[to - from + 1];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = ((from + i) / 20f).ToString(format, CultureInfo.InvariantCulture);
        }

        return texts;
    }

    /// <summary>One friend's controller as the tab shows it: name, its line, what came just now, what hasn't come (and whether that's none), and what came.</summary>
    private sealed record FriendRow(string Name, string Line, string JustNow, string NotYet, bool AllCame, string Given);

    /// <summary>The actions a friend's controller had given when the tab last looked, and those that came in the last seconds.</summary>
    private sealed class FriendSeen
    {
        private readonly HashSet<string> _known;
        private readonly List<(string Name, long At)> _recent = new();

        /// <summary>Starts from what it had given when the tab first saw it (none of those is "just now").</summary>
        public FriendSeen(IReadOnlyList<string> given) => _known = new HashSet<string>(given, StringComparer.Ordinal);

        /// <summary>Notes what it has given now: each action new since the last look is "just now" for a while.</summary>
        public void Note(IReadOnlyList<string> given, long now)
        {
            foreach (string action in given)
            {
                if (_known.Add(action))
                {
                    _recent.Add((action, now));
                }
            }

            _recent.RemoveAll(r => now - r.At > JustNowMs);
        }

        /// <summary>The actions that came in the last seconds, newest first, or "".</summary>
        public string Recent(long now)
        {
            var names = new List<string>(_recent.Count);
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                if (now - _recent[i].At <= JustNowMs)
                {
                    names.Add(_recent[i].Name);
                }
            }

            return names.Count == 0 ? string.Empty : ActionCoverage.Names(names, 160);
        }
    }
}
#endif
