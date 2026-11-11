#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using UnityEngine;
using RewiredPlayer = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// A friend's button prompts on the host's screen, named as their own couch would name them. On the couch each player's prompts
/// follow their own device and bindings; online the host's game names a friend's prompts from the host's own devices (a virtual
/// pad is no joystick, a virtual keyboard isn't the keyboard), so a friend's remaps never showed there. The friend's PC names
/// every action its own maps bind with the game's own matcher (<see cref="Make"/>) and sends the table
/// (<see cref="PromptNameTable"/>); the host answers the prompt pin's two name prefixes from it for that friend's Rewired player
/// (<see cref="TryAnswer"/>), and gives that friend's game player their device family in the game's prompt mapper
/// (<see cref="FollowFamily"/>). Never throws into the game: a failure is logged, and the host's answers are off for the rest of
/// the game's life after three.
/// </summary>
internal static class PromptNames
{
    private const int MaxFailureLogs = 3;

    private static readonly Dictionary<int, Friend> ByPlayer = new();
    private static readonly Dictionary<int, int> FamilySets = new();
    private static readonly Dictionary<int, InputAction> Actions = new();
    private static readonly HashSet<int> BoundScratch = new();
    private static Il2CppSystem.Collections.Generic.List<string>? _scratch;
    private static Il2CppSystem.Collections.Generic.IList<string>? _scratchView;
    private static int _failures;
    private static bool _off;
    private static bool _forcedLogged;

    /// <summary>True while any friend's table answers prompts on this PC.</summary>
    public static bool Any => ByPlayer.Count > 0;

    /// <summary>
    /// The friend's side: every action <paramref name="device"/>'s own maps bind, named on its family by the game's own
    /// matcher (<c>GetRawInputNameFromMaps</c>, as the prompt pin names this PC's prompts), each over the whole axis and each
    /// half. Null while the game's prompt mapper isn't up (asked again later). <paramref name="line"/> says what was made,
    /// for the log. With <paramref name="only"/>, just those actions' names over the whole axis are made (the host's own for the
    /// controller screen's three buttons, Campaign/Menus/ControllerGlyphViews.cs, remade while the screen is up: a half a button
    /// doesn't have is a "No mapping found" line in the game's log each time). Interop calls only; the caller catches.
    /// </summary>
    public static PromptNameTable? Make(PromptDevice device, ushort generation, out string line, IReadOnlyCollection<int>? only = null)
    {
        line = string.Empty;
        RewiredInputSpriteMapper? mapper = PromptPin.CurrentMapper();
        if (mapper == null || !Core.Input.PromptPinState.IsKnown((int)device.Family))
        {
            return null;
        }

        var took = Stopwatch.StartNew();
        Il2CppSystem.Collections.Generic.List<ControllerMap> maps = PromptPin.LoadMaps(device, out int player, out int bindings);
        Il2CppSystem.Collections.Generic.IList<ControllerMap> mapsView = maps.Cast<Il2CppSystem.Collections.Generic.IList<ControllerMap>>();
        _scratch ??= new Il2CppSystem.Collections.Generic.List<string>();
        _scratchView ??= _scratch.Cast<Il2CppSystem.Collections.Generic.IList<string>>();

        // Only actions the maps bind are asked: for any other the matcher finds nothing and writes "No mapping found" in
        // the game's own log (GetRawInputNameFromMaps), which every unbound action would fill.
        BoundScratch.Clear();
        for (int i = 0; i < maps.Count; i++)
        {
            foreach (ActionElementMap element in Il2CppLists.Items(maps[i]?.AllMaps))
            {
                if (element != null && element.actionId is >= 0 and <= InputBinding.MaxActionId)
                {
                    BoundScratch.Add(element.actionId);
                }
            }
        }

        var builder = new PromptNameTableBuilder();
        int asked = 0;
        int none = 0;
        var actions = new List<int>(BoundScratch);
        actions.Sort();
        var modifiers = new List<string>(PromptNameTable.MaxModifiers);
        foreach (int id in actions)
        {
            InputAction? action = only != null && !System.Linq.Enumerable.Contains(only, id) ? null : ActionOf(id);
            if (action == null)
            {
                continue;
            }

            int lastRange = only != null ? (int)BindingRange.Full : (int)BindingRange.Negative;
            for (int range = (int)BindingRange.Full; range <= lastRange; range++)
            {
                _scratch.Clear();
                asked++;
                string? name = mapper.GetRawInputNameFromMaps(action, mapsView, (AxisRange)range, device.Family, _scratchView);
                if (string.IsNullOrEmpty(name))
                {
                    none++;
                    continue;
                }

                modifiers.Clear();
                for (int m = 0; m < _scratch.Count; m++)
                {
                    modifiers.Add(_scratch[m]);
                }

                builder.Add(id, (BindingRange)range, name, modifiers);
            }
        }

        PromptNameTable table = builder.Build((byte)device.Family, generation);
        took.Stop();
        string what = device.Pad != null ? $"your pad \"{SafeText.Clean(device.Pad.name, 48)}\"" : "your keyboard and mouse";
        string skipped = builder.SkippedText + builder.SkippedFull + builder.SkippedRepeat + builder.ModifiersDropped == 0 ? string.Empty
            : $"; left out: {builder.SkippedText} name(s) that aren't plain text, {builder.SkippedFull} past the table's size, {builder.SkippedRepeat} repeated, " +
              $"{builder.ModifiersDropped} modifier key(s)";
        line = string.Create(CultureInfo.InvariantCulture,
            $"table {table.Generation}: {table.Entries.Count} name(s) for {actions.Count} bound action(s) on {Core.Input.PromptPinState.DeviceName((int)device.Family)}, " +
            $"from {what} ({maps.Count} map(s), {bindings} binding(s), Rewired player {player}'s saved maps or the game's defaults); {asked} asked of the game's matcher " +
            $"in {took.Elapsed.TotalMilliseconds:0.0} ms, {none} with none (each such ask is a \"No mapping found\" line in the game's own log){skipped}; {builder.Bytes} bytes");
        return table;
    }

    /// <summary>
    /// The host's side: from now on the prompts the game names for Rewired player <paramref name="rewiredPlayerId"/> (a
    /// friend's virtual controller) on <paramref name="table"/>'s family come from it. Replaces the table before.
    /// <paramref name="friendTag"/> names the friend in the log: their tag, never their Steam name (D127).
    /// </summary>
    public static void Use(int rewiredPlayerId, string friendTag, PromptNameTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (ByPlayer.TryGetValue(rewiredPlayerId, out Friend? friend) && friend.Tag == friendTag)
        {
            friend.Table = table;
            friend.FirstLogged = false;
            friend.BlankLogged = false;
            return;
        }

        ByPlayer[rewiredPlayerId] = new Friend(friendTag, table);
    }

    /// <summary>
    /// The friend on Rewired player <paramref name="rewiredPlayerId"/> went (their controller is gone): their prompts are
    /// the game's own again, and the log says what their table answered while they were here.
    /// </summary>
    public static void Forget(int rewiredPlayerId)
    {
        FamilySets.Remove(rewiredPlayerId, out int sets);
        if (!ByPlayer.Remove(rewiredPlayerId, out Friend? friend))
        {
            return;
        }

        ModLog.Info($"Prompts: {friend.Tag}'s prompts on this PC while they were here: {friend.Answered} drawn from their own bindings, {friend.Blank} blank (their maps bind " +
            $"nothing there, as on their couch), {friend.OtherFamily} asked on another family than their {Core.Input.PromptPinState.DeviceName(friend.Table.Family)} (this game's own); " +
            $"last table {friend.Table.Generation}; their family set on their game player {sets} time(s).");
    }

    /// <summary>
    /// The pin's name prefixes on a PC that isn't pinned (a host): a prompt the game names for a friend's Rewired player on
    /// their table's family is named from their table, modifier keys added to <paramref name="modifiers"/> as the game's
    /// matcher adds them; none bound gives null (a blank prompt, as the couch's "no mapping found"). True when answered (the
    /// game's own lookup is skipped). Never throws.
    /// </summary>
    public static bool TryAnswer(RewiredInputSpriteMapper mapper, RewiredPlayer? player, InputAction? action, AxisRange axisRange, EInputDeviceType device,
        Il2CppSystem.Collections.Generic.IList<string>? modifiers, ref string? result)
    {
        if (_off || ByPlayer.Count == 0 || player == null || action == null)
        {
            return false;
        }

        try
        {
            if (!ByPlayer.TryGetValue(player.id, out Friend? friend))
            {
                return false;
            }

            if ((int)device != friend.Table.Family)
            {
                friend.OtherFamily++;
                return false;
            }

            int actionId = action.id;
            if (!friend.Table.TryGet(actionId, (BindingRange)axisRange, out PromptName? name))
            {
                friend.Blank++;
                if (!friend.BlankLogged)
                {
                    friend.BlankLogged = true;
                    ModLog.Info($"Prompts: {friend.Tag}'s own bindings have nothing for \"{SafeText.Clean(action.name, 48)}\" ({axisRange}) on {Core.Input.PromptPinState.DeviceName(friend.Table.Family)}: " +
                        "that prompt is blank here, as on their couch. Further ones are counted.");
                }

                result = null;
                return true;
            }

            AddModifiers(modifiers, name!.Modifiers);
            friend.Answered++;
            result = name.Name;
            if (!friend.FirstLogged)
            {
                friend.FirstLogged = true;
                LogFirst(mapper, friend, player, action, name);
            }

            return true;
        }
        catch (Exception ex)
        {
            Fail("naming a friend's prompt", ex);
            return false;
        }
    }

    /// <summary>The names this PC holds for Rewired player <paramref name="rewiredPlayerId"/> (a friend's virtual controller), or null.</summary>
    public static PromptNameTable? TableOf(int rewiredPlayerId) => ByPlayer.TryGetValue(rewiredPlayerId, out Friend? friend) ? friend.Table : null;

    /// <summary>The modifier keys of a prompt's name, added to the list the game's matcher fills (as it adds its own).</summary>
    internal static void AddModifiers(Il2CppSystem.Collections.Generic.IList<string>? modifiers, IReadOnlyList<string> names)
    {
        if (names.Count > 0 && modifiers?.TryCast<Il2CppSystem.Collections.Generic.ICollection<string>>() is { } list)
        {
            foreach (string modifier in names)
            {
                list.Add(modifier);
            }
        }
    }

    /// <summary>
    /// The host's side, twice a second for each friend in the game: their game player <paramref name="gamePlayer"/> gets
    /// their device family in the game's prompt mapper when its entry is another one, as the couch's own detection gives
    /// each player theirs, unless the "Controller icons" setting forces an icon set (kept, as on the couch). The default
    /// entry (prompts tied to no player) goes back to what it was, last, so the game's two-axis prompts, which take every
    /// change's device (InputSpriteComposite.OnInputDeviceChanged), stay on it. <paramref name="friendTag"/> names the
    /// friend in the log: their tag, never their Steam name (D127). Never throws.
    /// </summary>
    public static void FollowFamily(int rewiredPlayerId, int gamePlayer, EInputDeviceType family, string friendTag)
    {
        if (_off || gamePlayer < 0 || !Core.Input.PromptPinState.IsKnown((int)family))
        {
            return;
        }

        try
        {
            RewiredInputSpriteMapper? mapper = PromptPin.CurrentMapper();
            if (mapper == null || gamePlayer >= mapper.MaxPlayers)
            {
                return;
            }

            int forced = PromptPin.ForcedDevice(mapper);
            if (forced >= 0)
            {
                if (!_forcedLogged)
                {
                    _forcedLogged = true;
                    ModLog.Info($"Prompts: this PC's Settings force the {Core.Input.PromptPinState.DeviceName(forced)} icon set on every player: friends' players keep it, as on the couch.");
                }

                return;
            }

            EInputDeviceType now = mapper.GetCurrentDeviceType(gamePlayer);
            if (now == family)
            {
                return;
            }

            EInputDeviceType defaultDevice = mapper.DefaultPlayerCurrentDevice;
            mapper.ChangeControllingDevice(family, gamePlayer, true);
            if (defaultDevice != family)
            {
                mapper.ChangeControllingDevice(defaultDevice, -1, true);
            }

            int sets = FamilySets.GetValueOrDefault(rewiredPlayerId) + 1;
            FamilySets[rewiredPlayerId] = sets;
            bool table = ByPlayer.ContainsKey(rewiredPlayerId);
            if (sets <= 3)
            {
                ModLog.Info($"Prompts: game player {gamePlayer} ({friendTag}) shows {Core.Input.PromptPinState.DeviceName((int)family)} prompts now (it showed " +
                    $"{Core.Input.PromptPinState.DeviceName((int)now)}), as their own couch would{(table ? string.Empty : " (their own bindings' names haven't come yet)")}" +
                    $"{(sets == 3 ? "; further changes are counted" : string.Empty)}.");
            }
        }
        catch (Exception ex)
        {
            Fail("giving a friend's player their prompt family", ex);
        }
    }

    // True when this game's sprite mapper has a sprite for the raw input name a friend sent (an input's own name).
    private static bool KnownInput(RewiredInputSpriteMapper mapper, string name, int family) =>
        mapper.GetSpriteForRawInput(name, (EInputDeviceType)family) != null;

    private static string LengthOf(string text) => string.Create(CultureInfo.InvariantCulture, $"a name of {text.Length} character(s)");

    /// <summary>The first prompt a friend's table answered: what, as what, for which game player.</summary>
    private static void LogFirst(RewiredInputSpriteMapper mapper, Friend friend, RewiredPlayer player, InputAction action, PromptName name)
    {
        try
        {
            Sprite? sprite = mapper.GetSpriteForRawInput(name.Name, (EInputDeviceType)friend.Table.Family);
            int gamePlayer = RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(player.id) : -1;
            // A friend's names are their game's text: written as they are only when this game's sprite mapper knows them (an input's
            // own name), else by their length.
            string modifiers = name.Modifiers.Count == 0 ? string.Empty
                : $" with {string.Join(" + ", name.Modifiers.Select(m => KnownInput(mapper, m, friend.Table.Family) ? SafeText.Clean(m, 48) : LengthOf(m)))}";
            string named = sprite != null ? $"\"{SafeText.Clean(name.Name, 48)}\"" : LengthOf(name.Name);
            ModLog.Info($"Prompts: \"{SafeText.Clean(action.name, 48)}\" for {friend.Tag} (Rewired player {player.id}, game player {gamePlayer}) drawn from their own bindings " +
                $"as {named}{modifiers} ({Core.Input.PromptPinState.DeviceName(friend.Table.Family)}, sprite " +
                $"{(sprite == null ? "none" : $"\"{SafeText.Clean(sprite.name, 48)}\"")}; table {friend.Table.Generation}). Further ones are counted.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Prompts: the line for {friend.Tag}'s first prompt failed ({ModLog.SafeErrorText(ex)}); the prompt itself is drawn.");
        }
    }

    /// <summary>The game's action <paramref name="id"/>, looked up once.</summary>
    private static InputAction? ActionOf(int id)
    {
        if (Actions.TryGetValue(id, out InputAction? action))
        {
            return action;
        }

        action = ReInput.mapping.GetAction(id);
        if (action != null)
        {
            Actions[id] = action;
        }

        return action;
    }

    /// <summary>A failure: logged (the first few with their stack); after three the host's answers are off (the game's own prompts), never into the game.</summary>
    private static void Fail(string what, Exception ex)
    {
        if (++_failures <= MaxFailureLogs)
        {
            ModLog.Error($"Prompts: {what} failed{(_failures == MaxFailureLogs ? "; friends' prompts on this PC are the game's own for the rest of this game's life" : string.Empty)}", ex);
        }

        if (_failures >= MaxFailureLogs)
        {
            _off = true;
        }
    }

    /// <summary>One friend's table on this PC and what it answered.</summary>
    private sealed class Friend
    {
        public Friend(string tag, PromptNameTable table)
        {
            Tag = tag;
            Table = table;
        }

        public string Tag { get; }

        public PromptNameTable Table { get; set; }

        public bool FirstLogged { get; set; }

        public bool BlankLogged { get; set; }

        public long Answered { get; set; }

        public long Blank { get; set; }

        public long OtherFamily { get; set; }
    }
}
#endif
