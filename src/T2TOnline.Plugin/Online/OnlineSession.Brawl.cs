#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppFighting;
using Il2CppRewired;
using T2TOnline.Core.Hosting;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Match;

namespace T2TOnline.Plugin.Online;

// Brawls with friends, one "Brawl:" line per friend at each brawl's end on both PCs, so a brawl's question (did a friend
// block, did the block reach the host's game, did it come in time) is answered by the logs. A brawl runs while the game's
// brawl HUD is up (FightHpBarSystem.IsBrawlActive, looked at ten times a second while a match plays: on a friend's PC the
// stream's brawl starts that HUD). Friend: the fight verbs their own device sent, by their own controls. Host, per friend:
// the fight verbs played into their controller (the same counting, so the two lines compare), how each AI punch's apex met
// their fighter (FightGrace: block already up, couldn't block, waited) and how each wait ended, with how late the next
// block came after a timed-out one. Counting a frame allocates nothing; the maps are built once a brawl.
internal sealed partial class OnlineSession
{
    /// <summary>The map category the game plays its forward controls (and so a brawl) in: SwitchToForwardControls switches to 0.</summary>
    private const int ForwardControlsCategory = 0;

    /// <summary>A brawl's line when its match ended before the brawl did.</summary>
    private const string MatchEndedDuringBrawl = "the match ended during it";

    private static readonly TimeSpan BrawlLookInterval = TimeSpan.FromMilliseconds(100);
    private static FightActionIds? _fightIds;

    // Host: per Rewired player of a friend's controller. Friend: _ownBrawl.
    private readonly Dictionary<int, BrawlFriend> _brawlFriends = new();
    private readonly BrawlRecord _ownBrawl = new();
    private FightInputMap? _ownBrawlMap;
    private bool _ownBrawlKeyboard;
    private bool _brawlOn;
    private TimeSpan _nextBrawlLook;
    private int _brawlFailures;

    // The match scene's guard's acts when this brawl began: one since means the guard took its HUD down as the match unloaded.
    private int _brawlGuardActs;

    /// <summary>Brawls followed here that ended with their match, not their own end (the check tour's <c>brawlend=game</c> counts them).</summary>
    internal int TourBrawlsCutShort { get; private set; }

    /// <summary>The fight verbs' action ids in this build (looked up by name once; -1 for one it doesn't have).</summary>
    private static FightActionIds FightIds => _fightIds ??= new FightActionIds(
        ReInput.mapping.GetActionId("Fight Block High"),
        ReInput.mapping.GetActionId("Fight Block Low"),
        ReInput.mapping.GetActionId("Fight Jab"),
        ReInput.mapping.GetActionId("Fight Uppercut"),
        ForwardControlsCategory);

    /// <summary>
    /// Host: brawls are followed for their lines (hosting friends, the match streamed to them). The look and an apex that
    /// comes before it both begin a brawl only with this and the brawl HUD up: an apex in a match that isn't streamed (its
    /// setup couldn't be captured, a campaign match not shared, the recorder stopped) or with the HUD down would otherwise
    /// begin a brawl ended a frame or a look later: a line per friend for every punch.
    /// </summary>
    private bool HostFollowsBrawls => _host != null && _pads.Count > 0 && _relay.HostStreaming;

    /// <summary>
    /// Every frame: whether a brawl runs in the match this PC hosts for friends or follows (looked at ten times a second),
    /// its start and end. Never throws: after three failures it stops for the session, said once.
    /// </summary>
    private void UpdateBrawl(TimeSpan elapsed, long now)
    {
        if (_brawlFailures >= 3)
        {
            return;
        }

        bool watching = HostFollowsBrawls || (_friend is { State: FriendSessionState.Joined } && _relay.PlayingHostMatch);
        if (!watching)
        {
            if (_brawlOn)
            {
                EndBrawl(now, MatchEndedDuringBrawl);
            }

            return;
        }

        if (elapsed < _nextBrawlLook)
        {
            return;
        }

        _nextBrawlLook = elapsed + BrawlLookInterval;
        try
        {
            bool on = FightHpBarSystem.IsBrawlActive;
            if (on && !_brawlOn)
            {
                BeginBrawl(now);
            }
            else if (!on && _brawlOn)
            {
                // The guard took the HUD down at the match scene's unload (this look can come before the relay sees the match gone).
                EndBrawl(now, BrawlLeftoverGuard.Acts != _brawlGuardActs ? MatchEndedDuringBrawl : null);
            }
        }
        catch (Exception ex)
        {
            BrawlFailed(ex);
        }
    }

    /// <summary>A brawl started (or an AI punch's apex met a friend before the look saw it): every record starts, every map is built.</summary>
    private void BeginBrawl(long now)
    {
        _brawlOn = true;
        _brawlGuardActs = BrawlLeftoverGuard.Acts;
        _brawlFriends.Clear();
        List<InputBinding>? template = null;
        if (_host != null)
        {
            foreach ((ulong steamId, RemotePad pad) in _pads)
            {
                if (pad.GamePlayer < 0)
                {
                    continue; // not in the game (waiting to join): no fighter of theirs
                }

                InputBindingSet? controls = _host.Find(steamId)?.Bindings;
                var friend = new BrawlFriend(pad.Tag, pad.GamePlayer, pad.Pad.IsKeyboard, MapOf(controls, pad.Pad.IsKeyboard, ref template));
                friend.Record.Begin(now);
                _brawlFriends[pad.Pad.RewiredPlayerId] = friend;
            }

            return;
        }

        _ownBrawlKeyboard = _localKeyboard != null;
        _ownBrawlMap = MapOf(_controls, _ownBrawlKeyboard, ref template);
        _ownBrawl.Begin(now);
    }

    /// <summary>
    /// A device's fight verbs from its controls: a keyboard's key table, a pad's own bindings, or the game's gamepad
    /// template for a pad on the game's defaults (read once a brawl). Null when a keyboard's controls haven't come.
    /// </summary>
    private static FightInputMap? MapOf(InputBindingSet? controls, bool keyboard, ref List<InputBinding>? template)
    {
        if (keyboard)
        {
            return controls is { Device: InputKind.KeyboardMouse } ? FightInputMap.Build(controls.Bindings, FightIds, controls) : null;
        }

        if (controls is { Device: InputKind.Gamepad, KeepsTemplateMaps: false })
        {
            return FightInputMap.Build(controls.Bindings, FightIds, null);
        }

        template ??= RewiredBindings.PadDefaults(VirtualPadDefinition.MapCategoryIds, out _);
        return FightInputMap.Build(template, FightIds, null);
    }

    /// <summary>The brawl ended (<paramref name="why"/>: null for its own end): one line per friend on the host, one on a friend.</summary>
    private void EndBrawl(long now, string? why)
    {
        if (!_brawlOn)
        {
            return;
        }

        _brawlOn = false;
        TourBrawlsCutShort += why == null ? 0 : 1;
        string during = why == null ? string.Empty : $"; {why}";
        try
        {
            foreach (BrawlFriend friend in _brawlFriends.Values)
            {
                friend.Record.End(now);
                string device = friend.Keyboard ? "keyboard" : "pad";
                string presses = friend.Map == null ? $"nothing that can be told apart (their {device}'s controls hadn't come)" : friend.Record.Presses.Describe(v => friend.Map.Where(v, RewiredBindings.ElementName));
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Brawl: {friend.Tag} (game player {friend.GamePlayer}) in this brawl ({friend.Record.Presses.DurationMicros / 1_000_000.0:0.0} s{during}): ") +
                    $"their {device} played here {presses}; {friend.Record.DescribeApexes()}.");
            }

            _brawlFriends.Clear();
            if (_ownBrawl.Presses.Running)
            {
                _ownBrawl.End(now);
                string device = _ownBrawlKeyboard ? "keyboard" : "pad";
                FightInputMap? map = _ownBrawlMap;
                string presses = map == null ? $"nothing that can be told apart (your {device}'s controls weren't read)" : _ownBrawl.Presses.Describe(v => map.Where(v, RewiredBindings.ElementName));
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Brawl: in {HostTag()}'s brawl ({_ownBrawl.Presses.DurationMicros / 1_000_000.0:0.0} s here{during}), ") +
                    $"your {device} sent {presses}.");
            }
        }
        catch (Exception ex)
        {
            BrawlFailed(ex);
        }
    }

    /// <summary>Host, every input update in a brawl: a friend's pad frame as played into their controller.</summary>
    private void NoteBrawlPad(int rewiredId, in PadState state, PadButtons taps, long now)
    {
        if (_brawlOn && _brawlFriends.TryGetValue(rewiredId, out BrawlFriend? friend) && friend.Map != null)
        {
            friend.Record.Feed(friend.Map.Held(state, taps), friend.Map.Tapped(taps), now);
        }
    }

    /// <summary>Host, every input update in a brawl: a keyboard friend's frame as played into their controller.</summary>
    private void NoteBrawlKeys(int rewiredId, ulong held, ulong taps, long now)
    {
        if (_brawlOn && _brawlFriends.TryGetValue(rewiredId, out BrawlFriend? friend) && friend.Map != null)
        {
            friend.Record.Feed(friend.Map.Held(held, taps), friend.Map.Tapped(taps), now);
        }
    }

    /// <summary>Friend, every input update in the host's brawl: this PC's pad frame as sent.</summary>
    private void NoteOwnBrawlPad(PadButtons held, float lx, float ly, float rx, float ry, float lt, float rt, long now)
    {
        if (_brawlOn && _ownBrawlMap != null && !_ownBrawlKeyboard)
        {
            var state = new PadState(held, 0, PadState.StickToRaw(lx), PadState.StickToRaw(ly), PadState.StickToRaw(rx), PadState.StickToRaw(ry),
                PadState.TriggerToRaw(lt), PadState.TriggerToRaw(rt));
            _ownBrawl.Feed(_ownBrawlMap.Held(state, PadButtons.None), FightActions.None, now);
        }
    }

    /// <summary>Friend, every input update in the host's brawl: this PC's keyboard frame as sent.</summary>
    private void NoteOwnBrawlKeys(ulong held, ulong pressed, long now)
    {
        if (_brawlOn && _ownBrawlMap != null && _ownBrawlKeyboard)
        {
            _ownBrawl.Feed(_ownBrawlMap.Held(held, pressed), _ownBrawlMap.Tapped(pressed), now);
        }
    }

    /// <summary>Host (FightGrace, from the game's apex): an AI punch met a friend's fighter.</summary>
    private void BrawlMet(int rewiredId, ApexMeeting meeting)
    {
        if (_brawlFailures >= 3)
        {
            return;
        }

        if (!_brawlOn && HostFollowsBrawls && FightHpBarSystem.IsBrawlActive)
        {
            BeginBrawl(Now); // the fight began since the last look (the game raises its brawl HUD as the fight starts)
        }

        if (_brawlFriends.TryGetValue(rewiredId, out BrawlFriend? friend))
        {
            friend.Record.Met(meeting);
        }
    }

    /// <summary>
    /// Host (FightGrace): a wait for a friend's block ended, <paramref name="waitedMicros"/> after it began and
    /// <paramref name="overdueMicros"/> after its window's end (FightGrace's clock): placed on this session's clock, where
    /// their presses are.
    /// </summary>
    private void BrawlWaitEnded(int rewiredId, GraceOutcome outcome, long waitedMicros, long overdueMicros)
    {
        if (_brawlFriends.TryGetValue(rewiredId, out BrawlFriend? friend))
        {
            long now = Now;
            friend.Record.WaitEnded(outcome, now - waitedMicros, now - overdueMicros);
        }
    }

    private void BrawlFailed(Exception ex)
    {
        if (++_brawlFailures == 3)
        {
            _brawlOn = false;
            _brawlFriends.Clear();
            ModLog.Warn($"Brawl: following brawls for their log lines failed three times ({ModLog.SafeErrorText(ex)}); no brawl lines this session.");
        }
    }

    /// <summary>A friend's part of one brawl on the host: who, and their record.</summary>
    private sealed class BrawlFriend
    {
        public BrawlFriend(string tag, int gamePlayer, bool keyboard, FightInputMap? map)
        {
            Tag = tag;
            GamePlayer = gamePlayer;
            Keyboard = keyboard;
            Map = map;
        }

        /// <summary>Their log tag.</summary>
        public string Tag { get; }

        public int GamePlayer { get; }

        public bool Keyboard { get; }

        public FightInputMap? Map { get; }

        public BrawlRecord Record { get; } = new();
    }
}
#endif
