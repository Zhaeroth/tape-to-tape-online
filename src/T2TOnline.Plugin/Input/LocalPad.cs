#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using Il2CppRewired.Data;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using Player = Il2CppRewired.Player;
using RewiredInputUtils = Il2CppTape2Tape.RewiredInputUtils;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// This PC's physical gamepad, taken away from the game's Rewired players so that only the mod reads it (the F8
/// loopback, or a friend's pad going to the host), and given back afterwards. It's read through Rewired's gamepad
/// template, so what's read doesn't depend on any map the game has active.
/// </summary>
internal sealed class LocalPad
{
    // Pads whose identity and template coverage were logged (once each).
    private static readonly HashSet<int> IdentityLogged = new();

    private readonly string? _hardwareIdentifier;

    // The pad's maps on the player it goes back to at the end: taken when found, and again after each lend (the friend may
    // have remapped it then: those are theirs now).
    private MapSnapshot? _maps;
    private int _lentTo = -1;
    private int _lastLentTo = -1;

    private LocalPad(GamepadTemplateReader reader, int ownerPlayerId, bool ownerJoined, IReadOnlyCollection<int>? ownerCategories, EInputDeviceType deviceType, MapSnapshot? maps)
    {
        _maps = maps;
        Reader = reader;
        _hardwareIdentifier = reader.Joystick.hardwareIdentifier;
        OwnerPlayerId = ownerPlayerId;
        OwnerJoined = ownerJoined;
        OwnerCategories = ownerCategories;
        DeviceType = deviceType;
    }

    /// <summary>Reads the pad's template elements.</summary>
    public GamepadTemplateReader Reader { get; private set; }

    /// <summary>The Rewired joystick.</summary>
    public Joystick Joystick => Reader.Joystick;

    /// <summary>The Rewired player the pad belonged to when found, or -1.</summary>
    public int OwnerPlayerId { get; }

    /// <summary>True when that player had joined the game.</summary>
    public bool OwnerJoined { get; }

    /// <summary>The map categories the pad had on for a joined owner; null otherwise.</summary>
    public IReadOnlyCollection<int>? OwnerCategories { get; }

    /// <summary>The pad family, for the game's button prompts and gamepad checks (a pad picked up again may be another family).</summary>
    public EInputDeviceType DeviceType { get; private set; }

    /// <summary>The pad the mod holds right now (detached from the game), if any.</summary>
    public static LocalPad? Held { get; private set; }

    /// <summary>True between <see cref="Detach"/> and giving it back.</summary>
    public bool IsDetached { get; private set; }

    /// <summary>The Rewired player the pad is lent to (<see cref="Lend"/>), or -1 while it isn't.</summary>
    public int LentTo => _lentTo;

    /// <summary>
    /// The Rewired player whose maps are this pad's controls: the one that had it, or for a pad nobody had, the one it was
    /// last lent to (its remapper edited that player's maps); -1 for a pad nobody had and never lent.
    /// </summary>
    public int ControlsPlayerId => OwnerPlayerId >= 0 ? OwnerPlayerId : _lastLentTo;

    /// <summary>True while the pad is plugged in.</summary>
    public bool IsConnected => Joystick.isConnected;

    /// <summary>
    /// The connected pad Rewired recognizes as a gamepad that this PC's player is using, with who owns it; null if there's
    /// none. With several, the one whose button was pressed last, else one a player of this game holds, else the first;
    /// the log says which and why.
    /// </summary>
    public static LocalPad? Find()
    {
        GamepadTemplateReader? reader = Choose(ReaderOf, out int candidates, out double readerPressed, out bool readerJoined);
        if (reader == null)
        {
            return null;
        }

        Joystick pad = reader.Joystick;
        if (IdentityLogged.Add(pad.id))
        {
            // What Rewired reads the pad as (Steam Input's pads are its "Steam Controller" profile) and whether its template has
            // every element a host's virtual pad takes: what's missing never reaches the host. Once per pad.
            string padText = $"Your gamepad \"{SafeText.Clean(pad.name, 48)}\" (hardware \"{SafeText.Clean(pad.hardwareName, 48)}\", {PadProfile.Describe(pad)})";
            ModLog.Info(reader.MissingElements > 0
                ? $"{padText} lacks {reader.MissingElements} of a standard pad's elements in Rewired's gamepad template ({reader.MissingNames()}); online they're never pressed."
                : $"{padText} has all {PadCheck.ElementCount} of a standard pad's elements in Rewired's gamepad template (RT included).");
        }

        if (candidates > 1)
        {
            ModLog.Info($"Your gamepads: {candidates} connected; using \"{pad.name}\" " +
                (readerPressed > 0 ? "(its button was pressed last)." : readerJoined ? "(a player of this game has it; no button pressed on any yet)." : "(the first; no button pressed on any yet)."));
        }

        int owner = OwnerOf(pad);
        Player? player = owner >= 0 ? ReInput.players.GetPlayer(owner) : null;
        bool ownerJoined = player != null && IsJoined(owner);
        IReadOnlyCollection<int>? categories = ownerJoined ? VirtualController.EnabledCategories(player!, ControllerType.Joystick, pad.id) : null;
        // Its maps as its owner has them, saved remaps included: checked when it's given back (MapSnapshot).
        MapSnapshot? maps = player != null ? TakeMaps(player, pad) : null;
        return new LocalPad(reader, owner, ownerJoined, categories, RewiredInputUtils.GetDeviceTypeFromController(pad), maps);
    }

    /// <summary>
    /// The pad <see cref="Find"/> would take now, chosen the same way, without taking it, reading its maps or logging (the
    /// panel's controller check): its reader, made by <paramref name="readerOf"/> (null for a joystick that isn't a
    /// connected gamepad), the Rewired player it's on (-1: none), and how many gamepads could be chosen. Null when there's none.
    /// </summary>
    public static GamepadTemplateReader? Peek(Func<Joystick, GamepadTemplateReader?> readerOf, out int owner, out int candidates)
    {
        GamepadTemplateReader? reader = Choose(readerOf, out candidates, out _, out _);
        owner = reader != null ? OwnerOf(reader.Joystick) : -1;
        return reader;
    }

    /// <summary>The family of the pad <see cref="Find"/> would take now, without taking it, reading its maps or logging; null when there's none.</summary>
    public static EInputDeviceType? PeekDeviceType()
    {
        GamepadTemplateReader? reader = Choose(ReaderOf, out _, out _, out _);
        return reader == null ? null : RewiredInputUtils.GetDeviceTypeFromController(reader.Joystick);
    }

    /// <summary>A reader for a connected gamepad, or null.</summary>
    private static GamepadTemplateReader? ReaderOf(Joystick joystick) => joystick.isConnected ? GamepadTemplateReader.TryCreate(joystick) : null;

    /// <summary>
    /// The connected gamepad this PC's player is using: the one whose button was pressed last; between pads never pressed
    /// (or pressed at the same moment), one a player of this game has; else the first.
    /// </summary>
    private static GamepadTemplateReader? Choose(Func<Joystick, GamepadTemplateReader?> readerOf, out int candidates, out double readerPressed, out bool readerJoined)
    {
        GamepadTemplateReader? reader = null;
        readerPressed = 0;
        readerJoined = false;
        candidates = 0;
        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            GamepadTemplateReader? candidate = readerOf(joystick);
            if (candidate == null)
            {
                continue;
            }

            // Pressed last wins; between pads never pressed (or pressed at the same moment), one a player of this game has.
            candidates++;
            double pressed = joystick.GetLastTimeAnyButtonPressed();
            bool joined = IsJoined(OwnerOf(joystick));
            int order = reader == null ? 1 : pressed.CompareTo(readerPressed);
            if (order > 0 || (order == 0 && joined && !readerJoined))
            {
                (reader, readerPressed, readerJoined) = (candidate, pressed, joined);
            }
        }

        return reader;
    }

    /// <summary>The pad's maps in its owner, or null (logged) if they couldn't be read: the pad still works, unchecked when it's back.</summary>
    private static MapSnapshot? TakeMaps(Player owner, Joystick pad)
    {
        try
        {
            return MapSnapshot.Take(owner, ControllerType.Joystick, pad.id);
        }
        catch (Exception ex)
        {
            ModLog.Error($"Your gamepad's maps on Rewired player {owner.id} couldn't be read; they won't be checked when it's given back", ex);
            return null;
        }
    }

    /// <summary>Takes the pad away from every Rewired player, so the game no longer reacts to it.</summary>
    public void Detach()
    {
        // First: should taking it fail, the session still reads it and KeepDetached tries again at the next input update.
        IsDetached = true;
        Held = this;
        ReInput.controllers.RemoveJoystickFromAllPlayers(Joystick, false);
    }

    /// <summary>
    /// While detached, takes the pad back from any Rewired player that has been given it since (Rewired or the game
    /// can assign a reconnected joystick). Returns the player it was taken from, or -1. Call every input update.
    /// </summary>
    public int KeepDetached()
    {
        // One question for the usual answer (nobody has it), before asking every player.
        if (!IsDetached || !Joystick.isConnected || !ReInput.controllers.IsJoystickAssigned(Joystick))
        {
            return -1;
        }

        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            if (ReInput.controllers.IsJoystickAssignedToPlayer(Joystick.id, id))
            {
                ReInput.controllers.RemoveJoystickFromAllPlayers(Joystick, false);
                return id;
            }
        }

        return -1;
    }

    /// <summary>
    /// After a disconnect: picks up a connected gamepad again (a reconnected pad is a new Rewired joystick, which
    /// Rewired may hand straight back to the game's players), takes it away from the game if this pad is detached,
    /// and reads it from now on. Returns true when there's a connected pad. Who owned the pad first is kept, so it
    /// still goes back there at the end. Called every half second while the pad is gone, and at once when a pad connects
    /// (<see cref="HeldPadGuard"/>).
    /// </summary>
    public bool TryReacquire()
    {
        if (Joystick.isConnected)
        {
            return true;
        }

        // Best first: the same kind of pad on no player; then the same kind of pad Rewired already gave to a player that
        // isn't in the game, or back to the one it had (our own pad, auto-assigned on reconnect); then any other pad on no
        // player or on one that isn't in the game. Never a pad another player of this game holds.
        GamepadTemplateReader? best = null;
        int bestRank = int.MaxValue;
        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            GamepadTemplateReader? reader = joystick.isConnected ? GamepadTemplateReader.TryCreate(joystick) : null;
            if (reader == null)
            {
                continue;
            }

            bool same = string.Equals(joystick.hardwareIdentifier, _hardwareIdentifier, System.StringComparison.Ordinal);
            int owner = OwnerOf(joystick);
            bool othersInGame = owner >= 0 && owner != OwnerPlayerId && IsJoined(owner);
            int rank = othersInGame ? int.MaxValue : same ? (owner < 0 ? 0 : 1) : 2;
            if (rank < bestRank)
            {
                best = reader;
                bestRank = rank;
            }
        }

        if (best == null)
        {
            return false;
        }

        Reader = best;
        DeviceType = RewiredInputUtils.GetDeviceTypeFromController(best.Joystick);
        if (IsDetached)
        {
            ReInput.controllers.RemoveJoystickFromAllPlayers(best.Joystick, false);
        }

        return true;
    }

    /// <summary>The first Rewired player <paramref name="joystick"/> is assigned to, or -1.</summary>
    private static int OwnerOf(Joystick joystick)
    {
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            if (ReInput.controllers.IsJoystickAssignedToPlayer(joystick.id, id))
            {
                return id;
            }
        }

        return -1;
    }

    /// <summary>True when Rewired player <paramref name="rewiredId"/> has joined this game (is a game player).</summary>
    private static bool IsJoined(int rewiredId) =>
        rewiredId >= 0 && RewiredPlayerInputAssigner.Instance != null && RewiredPlayerInputAssigner.GetPlayerId(rewiredId) >= 0;

    private void Release()
    {
        IsDetached = false;
        if (Held == this)
        {
            Held = null;
        }
    }

    /// <summary>Which of its buttons and axes were used since it was taken (said as it's given back).</summary>
    public PadUsage Usage { get; } = new();

    /// <summary>Reads the buttons and axes now.</summary>
    public (PadButtons Held, float LeftX, float LeftY, float RightX, float RightY, float LeftTrigger, float RightTrigger) Read()
    {
        var reading = (Reader.ReadButtons(),
            Reader.ReadAxis(PadAxis.LeftStickX),
            Reader.ReadAxis(PadAxis.LeftStickY),
            Reader.ReadAxis(PadAxis.RightStickX),
            Reader.ReadAxis(PadAxis.RightStickY),
            Reader.ReadAxis(PadAxis.LeftTrigger),
            Reader.ReadAxis(PadAxis.RightTrigger));
        Usage.Observe(reading.Item1, reading.Item2, reading.Item3, reading.Item4, reading.Item5, reading.Item6, reading.Item7);
        return reading;
    }

    /// <summary>
    /// Gives the pad back to the player that had it, as it had it (joined with its categories, or "press to join"). A pad
    /// nobody had goes where the game puts a newly connected pad: the first Rewired player without controllers, in "press
    /// to join". False when it's on no player afterwards (unplugged, or no player free): plugged in again, the game takes
    /// it as a new pad.
    /// </summary>
    public bool GiveBackToOwner()
    {
        if (OwnerPlayerId >= 0)
        {
            // Asked again: the game takes a player out when their pad is unplugged while it's lent to them, and a pad with only
            // a joined player's categories on couldn't press to join.
            return GiveBack(ReInput.players.GetPlayer(OwnerPlayerId), OwnerJoined && IsJoined(OwnerPlayerId) ? OwnerCategories : null);
        }

        Player? free = FirstPlayerWithoutControllers();
        if (free == null)
        {
            Release();
            ModLog.Warn("Your gamepad had no Rewired player before the session and none is free now; unplug it and plug it back in to use it here.");
            return false;
        }

        return GiveBack(free, null);
    }

    /// <summary>
    /// Gives the pad to <paramref name="player"/>: with <paramref name="categories"/> on for a joined player, or in the
    /// game's activation mode ("press to join") when null. Does nothing, and returns false, if the pad has been unplugged.
    /// </summary>
    public bool GiveBack(Player player, IReadOnlyCollection<int>? categories) => GiveBack(player, categories, lending: false);

    /// <summary>
    /// Lends the held pad back to this game for a moment (the friend's own remapper or settings, while joined): to the
    /// Rewired player that had it, or else the first one without controllers (where the game puts a new pad), with only
    /// <paramref name="categories"/> on (the UI's: the menus answer it, gameplay doesn't), its maps as before the session (or
    /// the saved ones, for a pad with no snapshot on that player). Returns that player, or -1 when it's unplugged or no player
    /// is free (it stays held then).
    /// </summary>
    public int Lend(IReadOnlyCollection<int> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (!IsDetached || !Joystick.isConnected)
        {
            return -1;
        }

        Player? player = OwnerPlayerId >= 0 ? ReInput.players.GetPlayer(OwnerPlayerId) : FirstPlayerWithoutControllers();
        if (player == null)
        {
            return -1;
        }

        if (!GiveBack(player, categories, lending: true))
        {
            Detach();
            return -1;
        }

        _lentTo = player.id;
        _lastLentTo = player.id;
        return player.id;
    }

    /// <summary>
    /// Takes the pad back after a lend (its menu closed): its maps on the player it was lent to are the ones the session's
    /// end puts back from now on (the friend may have remapped it), and it's taken off every player again. False when they
    /// couldn't be read (the earlier snapshot stays; logged).
    /// </summary>
    public bool TakeBack()
    {
        int lent = _lentTo;
        _lentTo = -1;
        bool fresh = true;
        if (lent >= 0 && Joystick.isConnected)
        {
            MapSnapshot? maps = TakeMaps(ReInput.players.GetPlayer(lent), Joystick);
            fresh = maps != null;
            _maps = maps ?? _maps;
        }

        Detach();
        return fresh;
    }

    /// <summary>
    /// The session ended while the pad was lent (its menu may still be open): it stays on the player it was lent to, set as
    /// a give-back sets it (a joined owner's categories, else "press to join"), its maps as the friend left them. False when
    /// it's on no player now (unplugged).
    /// </summary>
    public bool EndLend()
    {
        int lent = _lentTo;
        _lentTo = -1;
        Release();
        if (lent < 0 || !Joystick.isConnected)
        {
            return false;
        }

        bool joined = OwnerJoined && OwnerCategories != null && lent == OwnerPlayerId && IsJoined(lent);
        if (joined)
        {
            VirtualController.SetEnabledCategories(ReInput.players.GetPlayer(lent), ControllerType.Joystick, Joystick.id, OwnerCategories!);
        }
        else
        {
            RewiredPlayerInputAssigner.SetControlsToActivationMode(lent);
        }

        ModLog.Info($"Your gamepad stays on Rewired player {lent}, where it was lent, as the session ended" +
            (joined ? $", map categories [{string.Join(",", OwnerCategories!)}] on" : ", waiting to join") + $". Online it {Usage.Describe()}.");
        return true;
    }

    private bool GiveBack(Player player, IReadOnlyCollection<int>? categories, bool lending)
    {
        Release();
        if (!Joystick.isConnected)
        {
            ModLog.Info("Your gamepad is unplugged, so it isn't given to a player now; plugged in again, the game takes it as a new pad.");
            return false;
        }

        player.controllers.AddController(Joystick, true);
        string maps = string.Empty;
        if (_maps != null && _maps.PlayerId == player.id && _maps.ControllerId == Joystick.id)
        {
            // Rewired may load the pad's default maps as it's given back, dropping saved remaps until the game restarts. A
            // failure here mustn't stop the pad's categories below: it's logged, and the maps stay as Rewired gave them.
            try
            {
                int rebuilt = _maps.Restore(player, Joystick.id);
                maps = rebuilt == 0 ? $", its maps as before ({_maps.Describe()})" : $"; Rewired had reset {rebuilt} of its maps, restored as they were ({_maps.Describe()})";
            }
            catch (Exception ex)
            {
                ModLog.Error($"Your gamepad's maps on Rewired player {player.id} couldn't be checked against the snapshot; they stay as Rewired gave them back", ex);
                maps = ", its maps not checked (see the error above)";
            }
        }
        else
        {
            maps = LoadSavedMaps(player);
        }

        if (categories != null)
        {
            VirtualController.SetEnabledCategories(player, ControllerType.Joystick, Joystick.id, categories);
        }
        else
        {
            RewiredPlayerInputAssigner.SetControlsToActivationMode(player.id);
        }

        if (lending)
        {
            // Whether that player has a map of the asked categories for it at all: without one the menus don't hear it (the
            // mouse still works there), which only a test in the running game shows otherwise.
            IReadOnlyCollection<int> on = VirtualController.EnabledCategories(player, ControllerType.Joystick, Joystick.id);
            ModLog.Info($"Your gamepad is lent back to Rewired player {player.id} for your game's own menu{maps}, map categories [{string.Join(",", on)}] on " +
                (on.Count == 0 ? $"(it has no map of [{string.Join(",", categories!)}] there: the menu may not hear it; the mouse works)" : $"(asked: [{string.Join(",", categories!)}])") +
                "; the online session takes it again when the menu closes.");
            return true;
        }

        ModLog.Info($"Your gamepad is back on Rewired player {player.id}{maps}" +
            (categories == null ? ", waiting to join" : $", map categories [{string.Join(",", categories)}] on") + $". Online it {Usage.DescribeWithPeaks()}.");
        return true;
    }

    /// <summary>
    /// The pad just went on a player it has no snapshot for (a pad nobody had, or a new joystick after a reconnect or a swap):
    /// Rewired gives it that player's default maps (AddController never asks the data store), so the friend's saved
    /// remaps wouldn't be what the remapper shows, what's read back from the player and sent to the host, or (at the remapper's
    /// close, which saves) what stays on disk. They're loaded as at start-up; nothing changes where none are saved. Never
    /// throws: a failure is in the words it returns for the log line.
    /// </summary>
    private string LoadSavedMaps(Player player)
    {
        try
        {
            if (ReInput.userDataStore?.TryCast<UserDataStore_PlayerPrefs>() is not { } store)
            {
                return ", its saved maps not loaded (no player-prefs data store)";
            }

            store.LoadControllerData(player.id, ControllerType.Joystick, Joystick.id);
            return ", its saved maps loaded (or the game's defaults, if it has none)";
        }
        catch (Exception ex)
        {
            ModLog.Error($"Your gamepad's saved maps couldn't be loaded on Rewired player {player.id}; it has the game's defaults there until the next start", ex);
            return ", its saved maps not loaded (see the error above)";
        }
    }

    /// <summary>The first Rewired player with no controller at all (the game's connect handler gives a new pad to that one), or null.</summary>
    private static Player? FirstPlayerWithoutControllers()
    {
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            Player? player = ReInput.players.GetPlayer(id);
            Player.ControllerHelper? controllers = player?.controllers;
            if (controllers != null && controllers.joystickCount == 0 && controllers.customControllerCount == 0 && !controllers.hasKeyboard && !controllers.hasMouse)
            {
                return player;
            }
        }

        return null;
    }
}
#endif
