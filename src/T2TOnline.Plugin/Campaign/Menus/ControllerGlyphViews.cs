#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppBishopGames.UI;
using Il2CppControllerConnection;
using Il2CppRewired;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;
using T2TOnline.Plugin.Streaming;
using RewiredPlayer = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// Another player's own button names on their controller screen icon (v1; <see cref="ControllerGlyphs"/>). Each
/// icon draws three buttons (position lock, colour, jersey colour) from its player's own bindings; a friend's PC knows only
/// its own, so every other player's icon showed their pad family with this PC's default names.
/// <list type="bullet">
/// <item>Host: a row for each player on the screen with names (<see cref="HostRows"/>): a friend's from the names their PC sent
/// the host (message 22, which answer the host's own prompts for them), the host's own from the maps of the pad or the keyboard
/// the player holds (<see cref="PromptNames.Make"/>, just these three buttons). The rows ride in the screen's data; a name that
/// changes (a remap, a pad picked up, a friend joining) is noticed within a second (<see cref="HostTick"/>) and the screen goes
/// out again; a friend who joins has the board's newest.</item>
/// <item>Friend: an icon of another player has its three buttons refreshed with the game's own call while this class answers
/// the game's name lookup for that one button (<see cref="Scoped"/>, <see cref="TryAnswer"/>, from the prompt pin's prefix on the
/// mapper's two name functions), so the game itself composes the sprite from the player's raw input name and modifier keys, in
/// the device family the host draws that icon with. The friend's own icon is the prompt pin's, as before. When the game
/// doesn't ask the way expected, the icon keeps this PC's default names and the log says so; nothing is written anywhere.</item>
/// </list>
/// Nothing here throws into the game: callers guard, and a failure turns the module off for the game's life after three.
/// </summary>
internal static class ControllerGlyphViews
{
    private const double HostCheckSeconds = 1;

    // The host's own maps are made from its saved ones (a new instance per category) and can't change with this screen up: its
    // remapper is another menu, and a new pad is seen at once.
    private const double OwnNamesSeconds = 5;
    private const int SettleFrames = 30;
    private const int RecheckEvery = 15;
    private const int MaxRetries = 5;
    private const int MaxFailures = 3;
    private const int LinesPerMinute = 20;

    private static readonly LineBudget Lines = new("Menus (controller screen's button names)", LinesPerMinute);

    // Host
    private static readonly Dictionary<IntPtr, HostScreen> HostScreens = new();
    private static readonly Dictionary<int, OwnNames> Own = new();
    private static readonly HashSet<(int Slot, ulong Signature)> Said = new();
    private static readonly HashSet<string> Noted = new(StringComparer.Ordinal);
    private static int[]? _actionIds;
    private static bool _friends;
    private static double _nextCheck;

    // Friend
    private static readonly Dictionary<IntPtr, Followed> Shown = new();

    // The lookup being answered (a friend's, around one button's refresh).
    private static bool _active;
    private static int _family;
    private static int _button;
    private static GlyphName? _name;
    private static int _answered;

    private static int _failures;
    private static bool _off;

    /// <summary>True while a controller screen icon's button is being refreshed with its player's names (the pin's name lookups stand aside).</summary>
    public static bool Scoped => _active;

    // ------------------------------------------------------------------ host

    /// <summary>
    /// Host: the controller screen's data (<see cref="ControllerViews.Read"/>): a glyph row for each player on the screen whose
    /// own names are known (see the class). Nothing without friends in the session. Never throws.
    /// </summary>
    public static void HostRows(ControllerAssignmentMenu menu, List<MenuItem> items)
    {
        if (!_friends || _off)
        {
            return;
        }

        try
        {
            List<ControllerGlyphRow> rows = Rows(menu);
            HostScreens[menu.Pointer] = new HostScreen(menu, Combine(rows));
            foreach (ControllerGlyphRow row in rows)
            {
                items.Add(ControllerGlyphs.ToItem(row));
                if (Said.Add((row.Slot, row.Signature)) && Lines.TrySpend())
                {
                    ModLog.Info($"Menus: the host's controller screen sends {row.Describe()}: each friend's icon of this player draws their own button names.");
                }
            }

            if (Said.Count > 64)
            {
                Said.Clear();
            }
        }
        catch (Exception ex)
        {
            Failed("reading the players' button names", ex);
        }
    }

    /// <summary>
    /// Host, every frame of the menu views (a check a second): a name that changed since a screen was last read (a remap, a new
    /// pad, a friend's names that came) sends it again. <paramref name="friends"/>: friends are in the session.
    /// </summary>
    public static void HostTick(bool friends)
    {
        _friends = friends;
        if (!friends || _off || HostScreens.Count == 0)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + HostCheckSeconds;
        try
        {
            foreach (KeyValuePair<IntPtr, HostScreen> entry in HostScreens.ToList())
            {
                HostScreen screen = entry.Value;
                if (screen.Menu == null || screen.Menu.WasCollected || screen.Menu.IsHidden)
                {
                    HostScreens.Remove(entry.Key); // closed, or gone with its scene (a destroyed menu's fields still read as they were)
                    continue;
                }

                ulong signature = Combine(Rows(screen.Menu));
                if (signature != screen.Signature)
                {
                    screen.Signature = signature;
                    ControllerViews.MarkChanged(screen.Menu, "a player's button names");
                }
            }
        }
        catch (Exception ex)
        {
            Failed("checking the players' button names", ex);
        }
    }

    /// <summary>Host: hosting stopped.</summary>
    public static void ClearHost()
    {
        HostScreens.Clear();
        Own.Clear();
        Said.Clear();
        Noted.Clear();
        _actionIds = null;
        _friends = false;
    }

    /// <summary>The rows of the screen as it is now: one for each player it shows whose names are known and are for the family the host draws them with.</summary>
    private static List<ControllerGlyphRow> Rows(ControllerAssignmentMenu menu)
    {
        var rows = new List<ControllerGlyphRow>();
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
        InputSpriteMapper? drawn = MenuGame.Module()?.InputSpriteMapper;
        int[] actions = ActionIds();
        for (int i = 0; players != null && i < players.Count && i < ControllerScreenState.MaxIcons && drawn != null; i++)
        {
            ControllerAssignmentPlayer? icon = players[i];
            if (icon == null || icon.IsDisabled)
            {
                continue;
            }

            int slot = icon.GamePlayerId;
            if (slot < 0 || slot > ControllerScreenState.MaxSlot || slot >= drawn.MaxPlayers || RewiredPlayerInputAssigner.Instance == null)
            {
                continue;
            }

            int family = (int)drawn.GetCurrentDeviceType(slot);
            RewiredPlayer? player = RewiredPlayerInputAssigner.GetRewiredPlayer(slot);
            if (family < 0 || family > PromptNameTable.MaxFamily || player == null)
            {
                continue;
            }

            PromptNameTable? table = MenuPayloads.PadOwnerOf?.Invoke(slot) != null ? PromptNames.TableOf(player.id) : OwnNamesOf(slot, player, actions);
            if (table == null)
            {
                continue; // a friend whose names haven't come, an AI's icon, an empty one: this PC's default names stay
            }

            if (table.Family != family)
            {
                Note($"family {slot}", $"Menus: the host's controller screen draws P{slot + 1}'s buttons as {PromptPinState.DeviceName(family)} but their names are for " +
                    $"{PromptPinState.DeviceName(table.Family)}; friends keep their default names for it until the two are the same.");
                continue;
            }

            ControllerGlyphRow? row = ControllerGlyphs.From(slot, table, actions);
            if (row != null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// The host's own player's names for the three buttons: from the maps of the pad the player holds (its saved maps, the
    /// game's where there are none), or the keyboard's; made again every <see cref="OwnNamesSeconds"/> (a remap shows within
    /// them), null for a player with neither or while the game's prompt mapper isn't up.
    /// </summary>
    private static PromptNameTable? OwnNamesOf(int slot, RewiredPlayer player, int[] actions)
    {
        double now = FrameClock.Seconds;
        PromptDevice? device = DeviceOf(player);
        if (device == null)
        {
            Own.Remove(slot);
            return null;
        }

        PromptDevice held = device.Value;
        IntPtr pad = held.Pad?.Pointer ?? IntPtr.Zero;
        if (Own.TryGetValue(slot, out OwnNames? known) && known.Family == (int)held.Family && known.Pad == pad && now - known.At < OwnNamesSeconds)
        {
            return known.Table;
        }

        PromptNameTable? table = PromptNames.Make(held, 1, out _, actions.Where(a => a >= 0).ToArray());
        Own[slot] = new OwnNames((int)held.Family, pad, now, table);
        return table;
    }

    /// <summary>The device a host player holds: its first pad (its own family), else the keyboard and mouse when it has them; null for neither.</summary>
    private static PromptDevice? DeviceOf(RewiredPlayer player)
    {
        foreach (Joystick joystick in ReInput.controllers.GetJoysticks())
        {
            if (joystick != null && ReInput.controllers.IsJoystickAssignedToPlayer(joystick.id, player.id))
            {
                return PromptDevice.ForHeldPad(RewiredInputUtils.GetDeviceTypeFromController(joystick), joystick, player.id);
            }
        }

        return player.controllers.hasKeyboard ? PromptDevice.ForKeyboard(PromptDevice.KeyboardPlayerId) : null;
    }

    /// <summary>The Rewired actions of the icon's three buttons in this build, looked up by name once (-1: this build has none).</summary>
    private static int[] ActionIds()
    {
        if (_actionIds != null)
        {
            return _actionIds;
        }

        var all = new List<(int Id, string Name)>();
        foreach (int id in RewiredBindings.ActionIds())
        {
            string? name = ReInput.mapping.GetAction(id)?.name;
            if (name != null)
            {
                all.Add((id, name));
            }
        }

        var ids = new int[ControllerGlyphs.Buttons];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = ControllerGlyphs.FindAction(i, all);
            if (ids[i] < 0)
            {
                Note($"action {i}", $"Menus: this build has no Rewired action called \"{ControllerGlyphs.ActionNames[i]}\" (the controller screen's {ButtonWords[i]} button); that button keeps this PC's default name on friends' icons.");
            }
        }

        Note("actions", $"Menus: the controller screen's position lock, colour and jersey colour buttons are Rewired actions " +
            $"{string.Join(", ", ids.Select(id => id < 0 ? "(none)" : $"\"{SafeText.Clean(ReInput.mapping.GetAction(id)?.name, 32)}\" ({id.ToString(System.Globalization.CultureInfo.InvariantCulture)})"))} in this build.");
        _actionIds = ids;
        return ids;
    }

    private static readonly string[] ButtonWords = { "position lock", "colour", "jersey colour" };

    private static ulong Combine(List<ControllerGlyphRow> rows)
    {
        ulong hash = (ulong)rows.Count;
        foreach (ControllerGlyphRow row in rows)
        {
            hash = (hash * 1099511628211UL) ^ row.Signature;
        }

        return hash;
    }

    // ------------------------------------------------------------------ friend

    /// <summary>
    /// Friend, every frame of a real controller screen (before the host's state goes into it): the host's glyph rows are read
    /// when its data changed, and an icon of another player whose names are new, or whose buttons the game drew again since, is
    /// refreshed with them. The screen's own icon (<paramref name="self"/>'s) and disabled ones are left to the game and the
    /// pin. <paramref name="frame"/> is the view's frame (1 at its first). Never throws.
    /// </summary>
    public static void Follow(ControllerAssignmentMenu open, ControllerScreenState? screen, MenuData data, ulong self, int frame)
    {
        if (_off)
        {
            return;
        }

        try
        {
            if (frame <= 1)
            {
                Shown.Remove(open.Pointer); // a copy the game kept, opened again: its icons begin afresh (the view's first frame has no state yet)
            }

            if (screen == null)
            {
                return;
            }

            if (!Shown.TryGetValue(open.Pointer, out Followed? followed))
            {
                if (Shown.Count >= 4)
                {
                    Shown.Clear();
                }

                Shown[open.Pointer] = followed = new Followed();
            }

            bool changed = followed.DataHash != data.ContentHash || frame <= 1;
            bool recheck = frame <= SettleFrames || frame % RecheckEvery == 0;
            if (changed)
            {
                followed.DataHash = data.ContentHash;
                followed.Rows = ControllerGlyphs.TryRead(data, out int refused);
                if (refused > 0 && Noted.Add($"refused {refused}"))
                {
                    ModLog.Warn($"Menus: {refused} of {LogNames.HostTag()}'s glyph row(s) on the controller screen didn't read here; those icons keep this PC's default names.");
                }
            }

            if ((!changed && !recheck) || (followed.Rows.Count == 0 && followed.Icons.Count == 0))
            {
                return;
            }

            Apply(open, screen, self, frame, followed);
        }
        catch (Exception ex)
        {
            Failed("drawing the players' own button names", ex);
        }
    }

    /// <summary>
    /// Friend: the plan drew <paramref name="icon"/>'s buttons again for <paramref name="device"/> (<see cref="ControllerViews.Run"/>):
    /// its player's names go over them. Never throws.
    /// </summary>
    public static void Drawn(ControllerAssignmentPlayer icon, int device)
    {
        if (_off || Shown.Count == 0)
        {
            return;
        }

        try
        {
            ControllerAssignmentMenu? menu = icon.Parent;
            if (menu == null || !Shown.TryGetValue(menu.Pointer, out Followed? followed) || followed.Own.Contains(icon.GamePlayerId))
            {
                return;
            }

            ControllerGlyphRow? row = followed.Rows.FirstOrDefault(r => r.Slot == icon.GamePlayerId);
            if (row != null && row.Family == device && followed.Icons.TryGetValue(row.Slot, out Applied? applied))
            {
                Draw(icon, row, applied);
            }
        }
        catch (Exception ex)
        {
            Failed("drawing a player's own button names over the plan's", ex);
        }
    }

    private static void Apply(ControllerAssignmentMenu open, ControllerScreenState screen, ulong self, int frame, Followed followed)
    {
        followed.Own.Clear();
        var wanted = new HashSet<int>();
        foreach ((ControllerAssignmentPlayer icon, ControllerIconShown shown) in ControllerViews.Icons(open))
        {
            int slot = shown.Slot;
            if (!screen.TryGet(slot, out ControllerIcon hostIcon) || hostIcon.Off)
            {
                continue;
            }

            if (self != 0 && hostIcon.Steam == self)
            {
                followed.Own.Add(slot); // this player's own icon: the prompt pin draws it from their own bindings
                continue;
            }

            ControllerGlyphRow? row = followed.Rows.FirstOrDefault(r => r.Slot == slot);
            if (row == null)
            {
                continue;
            }

            if (hostIcon.Device != row.Family)
            {
                // The host draws this icon in another family than its player's names are for: its own screen shows the default names too.
                Note($"family {slot} {hostIcon.Device} {row.Family}", $"Menus: {LogNames.HostTag()}'s screen draws P{slot + 1}'s buttons as {PromptPinState.DeviceName(hostIcon.Device)} but their names are for " +
                    $"{PromptPinState.DeviceName(row.Family)}; their icon keeps this PC's default names for it, as the host's does.");
                continue;
            }

            wanted.Add(slot);
            if (!followed.Icons.TryGetValue(slot, out Applied? applied))
            {
                followed.Icons[slot] = applied = new Applied();
            }

            ulong signature = row.Signature; // a hash over the row's names: worked out once per icon per pass
            bool stale = applied.Signature != signature || (!applied.Settled && frame % RecheckEvery == 0 && applied.Retries < MaxRetries) || Redrawn(icon, applied);
            if (stale)
            {
                if (applied.Signature == signature)
                {
                    applied.Retries++;
                }
                else
                {
                    applied.Retries = 0;
                }

                Draw(icon, row, applied);
                applied.Signature = signature;
                if (!applied.Settled && applied.Retries >= MaxRetries)
                {
                    Note($"gave up {slot}", $"Menus: the game never asked for all of P{slot + 1}'s button names when their icon was refreshed here ({MaxRetries} tries); those keep this PC's default names.");
                }
            }
        }

        foreach (int slot in followed.Icons.Keys.Where(s => !wanted.Contains(s)).ToList())
        {
            followed.Icons.Remove(slot); // its player went, or its names did: the game's own prompt (an icon the screen redraws itself next)
            RedrawDefault(open, slot);
        }
    }

    /// <summary>The three buttons of <paramref name="icon"/> refreshed one at a time with the lookup answered from <paramref name="row"/>.</summary>
    private static void Draw(ControllerAssignmentPlayer icon, ControllerGlyphRow row, Applied applied)
    {
        InputSpriteUI?[] buttons = { icon.m_PositionLockInputSprite, icon.m_ChangeColorInputSprite, icon.m_ChangeJerseyColorInputSprite };
        int answered = 0;
        int unknown = 0;
        int hidden = 0;
        for (int i = 0; i < buttons.Length; i++)
        {
            InputSpriteUI? button = buttons[i];
            if (button == null || button.WasCollected)
            {
                applied.Shown[i] = IntPtr.Zero;
                hidden++;
                continue;
            }

            GlyphName? name = row.Names[i];
            button.m_ForceDeviceType = true;
            button.m_DeviceType = (EInputDeviceType)row.Family;
            if (!button.isActiveAndEnabled)
            {
                // Hidden (the game refreshes it when it shows: Redrawn sees that and draws it with the names then).
                applied.Shown[i] = IntPtr.Zero;
                hidden++;
                continue;
            }

            _family = row.Family;
            _button = i;
            _name = name;
            _answered = 0;
            _active = true;
            try
            {
                button.Refresh();
            }
            finally
            {
                _active = false;
                _name = null;
            }

            if (name == null)
            {
                unknown++;
            }
            else if (_answered > 0)
            {
                answered++;
            }

            applied.Shown[i] = SpriteOf(button);
        }

        int expected = buttons.Length - unknown - hidden; // the buttons shown, with a name from the host
        applied.Settled = answered >= expected;
        if (!applied.Settled && applied.Retries == 0 && Lines.TrySpend())
        {
            ModLog.Info($"Menus: P{row.Slot + 1}'s buttons were refreshed here with their own names but the game asked for {answered} of {expected} (it deferred or asked another way); " +
                "the rest keep this PC's default names, and are tried again shortly.");
        }
        else if (applied.Settled && answered > 0 && applied.Retries == 0 && Lines.TrySpend())
        {
            ModLog.Info($"Menus: P{row.Slot + 1}'s buttons are drawn here from their own bindings, as on the host's screen: {row.Describe()}.");
        }
    }

    /// <summary>True when the game drew one of the icon's buttons again since <paramref name="applied"/> (their sprite isn't the one set), or one hidden then is shown now.</summary>
    private static bool Redrawn(ControllerAssignmentPlayer icon, Applied applied)
    {
        InputSpriteUI?[] buttons = { icon.m_PositionLockInputSprite, icon.m_ChangeColorInputSprite, icon.m_ChangeJerseyColorInputSprite };
        for (int i = 0; i < buttons.Length; i++)
        {
            InputSpriteUI? button = buttons[i];
            if (button != null && !button.WasCollected && (applied.Shown[i] == IntPtr.Zero ? button.isActiveAndEnabled && applied.Retries < MaxRetries : SpriteOf(button) != applied.Shown[i]))
            {
                return true; // drawn again by the game, or shown since it was last left alone (hidden then)
            }
        }

        return false;
    }

    /// <summary>An icon no longer drawn from its player's names: its buttons are refreshed by the game's own lookup again.</summary>
    private static void RedrawDefault(ControllerAssignmentMenu open, int slot)
    {
        foreach ((ControllerAssignmentPlayer icon, ControllerIconShown shown) in ControllerViews.Icons(open))
        {
            if (shown.Slot == slot)
            {
                foreach (InputSpriteUI? button in new[] { icon.m_PositionLockInputSprite, icon.m_ChangeColorInputSprite, icon.m_ChangeJerseyColorInputSprite })
                {
                    if (button != null && !button.WasCollected)
                    {
                        button.Refresh();
                    }
                }
            }
        }
    }

    private static IntPtr SpriteOf(InputSpriteUI button) => button.m_Image?.overrideSprite?.Pointer ?? IntPtr.Zero;

    /// <summary>
    /// The pin's name lookup for the button being refreshed: the player's own name (and modifier keys), when the game asks for
    /// the button's action over the full axis in the family the names are for. False otherwise: the game's own lookup runs,
    /// and what it asked is counted for the log.
    /// </summary>
    public static bool TryAnswer(InputAction? action, AxisRange axisRange, EInputDeviceType device, Il2CppSystem.Collections.Generic.IList<string>? modifiers, ref string? result)
    {
        GlyphName? name = _name;
        if (name == null)
        {
            return false;
        }

        if ((int)device != _family || (int)axisRange != (int)BindingRange.Full || !ControllerGlyphs.IsAction(_button, action?.name))
        {
            Note($"asked {_button} {(int)device} {(int)axisRange} {action?.name}", $"Menus: a controller screen icon's {ButtonWords[_button]} button asked for \"{SafeText.Clean(action?.name, 32)}\" over " +
                $"{axisRange} in {PromptPinState.DeviceName((int)device)}, not what was expected (\"{ControllerGlyphs.ActionNames[_button]}\", the full axis, {PromptPinState.DeviceName(_family)}); " +
                "it keeps this PC's default name.");
            return false;
        }

        PromptNames.AddModifiers(modifiers, name.Modifiers);
        result = name.IsBlank ? null : name.Name;
        _answered++;
        return true;
    }

    /// <summary>Friend: following stopped.</summary>
    public static void ClearFriend() => Shown.Clear();

    // ------------------------------------------------------------------ both

    /// <summary>A line said once per kind (the key), in the log's budget.</summary>
    private static void Note(string key, string line)
    {
        if (Noted.Add(key) && Lines.TrySpend())
        {
            ModLog.Info(line);
        }

        if (Noted.Count > 128)
        {
            Noted.Clear();
        }
    }

    private static void Failed(string what, Exception ex)
    {
        _failures++;
        ModLog.Error($"Menus: {what} failed{(_failures >= MaxFailures ? "; the controller screen's icons keep this PC's default button names for the rest of this game's life" : string.Empty)}", ex);
        if (_failures >= MaxFailures)
        {
            _off = true;
            _active = false;
            _name = null;
        }
    }

    /// <summary>Host: a screen sent, and the rows' signature it was sent with.</summary>
    private sealed class HostScreen
    {
        public HostScreen(ControllerAssignmentMenu menu, ulong signature)
        {
            Menu = menu;
            Signature = signature;
        }

        public ControllerAssignmentMenu Menu { get; }

        public ulong Signature { get; set; }
    }

    /// <summary>Host: the names of one of its own players, made at a time.</summary>
    private sealed record OwnNames(int Family, IntPtr Pad, double At, PromptNameTable? Table);

    /// <summary>Friend: a screen shown for the host, with its glyph rows and what has been drawn on its icons.</summary>
    private sealed class Followed
    {
        public ulong DataHash { get; set; }

        public IReadOnlyList<ControllerGlyphRow> Rows { get; set; } = Array.Empty<ControllerGlyphRow>();

        public Dictionary<int, Applied> Icons { get; } = new();

        /// <summary>The slots of this player's own icon on the screen (the prompt pin's).</summary>
        public HashSet<int> Own { get; } = new();
    }

    /// <summary>Friend: what was drawn on one icon: the row (by its signature), the sprite each button showed after, and whether the game asked for all of them.</summary>
    private sealed class Applied
    {
        public ulong Signature { get; set; }

        public IntPtr[] Shown { get; } = new IntPtr[ControllerGlyphs.Buttons];

        public bool Settled { get; set; }

        public int Retries { get; set; }
    }
}
#endif
