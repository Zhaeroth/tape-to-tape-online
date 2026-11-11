#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppData;
using Il2CppI2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue;
using Il2CppRogue.NPC.NurseNPC;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppSkaters.Injuries;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Customization.UI;
using Il2CppUtilsMono;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using T2TOnline.Plugin.Startup;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// An event choice's translated texts in each player's own language. The game makes some of a choice's texts in the host's language
/// as it rolls them (a power-up's name and description, a stat's name, a Nurse procedure's card, a format around a skater's name):
/// a friend's copy of the choice would show them in the host's language.
/// <list type="bullet">
/// <item>Host (<see cref="Host"/>): each text's recipe (<see cref="TextRecipe"/>, from the producers' table in
/// <see cref="EventTextRecipes"/>), made on the host's PC with the game's own functions over the choice's own objects; only
/// one that makes the host's text exactly goes. A text no recipe makes goes as the host's (a name or a number needs none;
/// anything else is logged once per event class and slot).</item>
/// <item>Friend (<see cref="Localize"/>): each recipe made on this PC, in its language, with the game's same functions over
/// the objects built here for the choice (the same ids and levels); one that makes nothing keeps the host's text (logged
/// once per kind).</item>
/// </list>
/// </summary>
internal static class EventTexts
{
    private const int TextChars = 60;

    // Most recipes said to have failed here (a host's words, as many as it likes): past this the build's line only counts them.
    private const int MaxFailedSaid = 64;

    // What's been said once: a slot with no recipe (by event class and slot), a recipe that failed here (by its words).
    private static readonly HashSet<string> SaidUntranslated = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SaidFailed = new(StringComparer.Ordinal);

    /// <summary>
    /// Host: <paramref name="data"/> (the host's choice as read) with the recipe of each text one makes exactly here.
    /// <paramref name="agent"/>: the choice's free agent, or null. Never throws: a failure leaves the texts as the host's.
    /// </summary>
    public static EventChoiceData Host(string eventClass, EventNode node, EventChoice choice, ForwardData? agent, EventChoiceData data, EventTextTally tally)
    {
        if (data.TranslatedButtonName == null && data.Texts == null)
        {
            return data;
        }

        try
        {
            var functions = new GameTextFunctions(choice.PowerUpOrRelic, choice.PowerUpOrRelic2, agent);
            NurseOffer? nurse = eventClass == EventTextRecipes.NurseClass ? NurseCards.Offer(node) : null;
            return EventTextRecipes.WithRecipes(eventClass, data, functions, SkaterName(choice.Skater), nurse, tally);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: a {EventCapture.Words(eventClass)} choice's texts couldn't be matched to the game's own ({ModLog.SafeErrorText(ex)}); friends see them in the host's language.");
            return data;
        }
    }

    /// <summary>Host: one set's texts and images, said once it's read (a slot with no recipe once per event class and slot).</summary>
    public static void Said(EventChoiceSet set, EventTextTally tally, EventImages.HostSprites sprites)
    {
        if (tally.Recipes + tally.Neutral + tally.Untranslated.Count + sprites.Exact + sprites.Named == 0)
        {
            return;
        }

        ModLog.Info($"Menus: the host's {EventCapture.Words(set.EventClass)} texts (v{set.Version}): {tally.Recipes} go as recipes (each friend makes them in its own language), " +
            $"{tally.Neutral} are names or numbers, {tally.Untranslated.Count} kept in the host's language; images: {sprites.Exact} by their exact source, {sprites.Named} by name.");
        foreach ((string slot, string text) in tally.Untranslated)
        {
            if (SaidUntranslated.Add($"{set.EventClass}/{slot}"))
            {
                // The text itself only for a developer (the line is for finding recipes to add): it can name a skater.
                string shown = ModSettings.DevTools ? $"\"{MenuGame.Plain(text, TextChars)}\"" : LogNames.TextShape(text);
                ModLog.Info($"Menus: a {EventCapture.Words(set.EventClass)} choice's {slot} has no recipe in this version; friends see it in the host's language ({shown}).");
            }
        }
    }

    /// <summary>
    /// Friend: <paramref name="data"/> with each text that has a recipe made on this PC, over the objects built here for the
    /// choice (<paramref name="object1"/>, <paramref name="object2"/>, the free agent <paramref name="agent"/>). Never throws: a
    /// failure keeps the host's texts.
    /// </summary>
    public static EventChoiceData Localize(EventChoiceData data, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent, EventTextTally tally)
    {
        if (data.ButtonRecipe == null && data.TextRecipes == null)
        {
            return data;
        }

        int failed = tally.Failed.Count;
        EventChoiceData made;
        try
        {
            made = EventTextRecipes.Localize(data, new GameTextFunctions(object1, object2, agent), tally);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: EventChoice: the host's texts couldn't be made in this PC's language ({ModLog.SafeErrorText(ex)}); the host's are shown.");
            return data;
        }

        for (int i = failed; i < tally.Failed.Count; i++)
        {
            if (SaidFailed.Count < MaxFailedSaid && SaidFailed.Add(tally.Failed[i]))
            {
                // The recipe as its own log text (TextRecipe.ToString): what it is built from, with a literal that has a letter
                // (a button's "... for <name>") as its length and a term or an asset by its key, so it names nobody.
                ModLog.Warn($"Menus: EventChoice: the host's recipe {SafeText.Clean(tally.Failed[i], 160)} made nothing in this PC's language; the host's text is shown.");
            }
        }

        return made;
    }

    /// <summary>A skater's name as the events write it (<c>firstName + " " + lastName</c>), or null for none.</summary>
    private static string? SkaterName(SkaterData? skater) => skater == null ? null : string.Concat(skater.firstName, " ", skater.lastName);
}

/// <summary>
/// The game's own text functions (<see cref="ITextFunctions"/>), on this PC and in its language, over one event choice's
/// objects: its power-up or relic, its second one and its free agent (each as this PC has it). Each returns null for an
/// object the choice hasn't or a value this game doesn't know; what the game throws, the recipe catches.
/// </summary>
internal sealed class GameTextFunctions : ITextFunctions
{
    private readonly ScriptableObject? _object1;
    private readonly ScriptableObject? _object2;
    private readonly ForwardData? _agent;

    public GameTextFunctions(ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
    {
        _object1 = object1;
        _object2 = object2;
        _agent = agent;
    }

    /// <summary>The term's text, exactly as the events translate theirs (verified in the game's code: every producer's
    /// GetTranslation call).</summary>
    public string? Term(string key) => LocalizationManager.GetTranslation(key, true, 0, true, false, null, null, true);

    /// <summary>
    /// The game's <c>SafeStringFormatter.SafeFormat</c> with the arguments as strings (as every producer passes them), each
    /// fresh string stored through the write barrier (<see cref="GameArrays"/>): the array is the only thing holding it.
    /// </summary>
    public string? Format(string format, IReadOnlyList<string> args)
    {
        var array = new Il2CppReferenceArray<Il2CppSystem.Object>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            GameArrays.Store(array, i, IL2CPP.ManagedStringToIl2Cpp(args[i]));
        }

        return SafeStringFormatter.SafeFormat(format, array);
    }

    /// <summary>
    /// A power-up's <c>localizedPowerupName</c> or a relic's <c>localizedRelicName</c> (each virtual: the object's own; a relic
    /// isn't a power-up, verified in a dump of the game's types: <c>Rogue.Relic : ScriptableObject</c>; the events' name calls are
    /// the same vtable slot).
    /// </summary>
    public string? ObjectName(ChoiceObjectRef target)
    {
        ScriptableObject? value = Resolve(target);
        return value?.TryCast<PowerUp>()?.localizedPowerupName ?? value?.TryCast<Relic>()?.localizedRelicName;
    }

    /// <summary>A power-up's <c>GetDescription</c> (virtual: the object's own) or a relic's.</summary>
    public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex)
    {
        ScriptableObject? value = Resolve(target);
        return value?.TryCast<PowerUp>()?.GetDescription(includeUpgrade, includeStatBonus, colorHex) ?? value?.TryCast<Relic>()?.GetDescription(includeUpgrade, includeStatBonus, colorHex);
    }

    /// <summary>The game's <c>UISkaterUtils.GetForwardStat</c>.</summary>
    public string? StatName(int stat, bool description) =>
        Enum.IsDefined(typeof(EForwardStat), stat) ? UISkaterUtils.GetForwardStat((EForwardStat)stat, description) : null;

    /// <summary>The game's <c>NurseProcedureScreen.GetProcedureName</c>.</summary>
    public string? NurseProcedureName(int procedure) =>
        Enum.IsDefined(typeof(NurseProcedureState.Procedure), procedure) ? NurseProcedureScreen.GetProcedureName((NurseProcedureState.Procedure)procedure) : null;

    /// <summary>The game's <c>NurseProcedureScreen.GetCardDescription</c> (<see cref="NurseCards.Describe"/>).</summary>
    public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames)
    {
        if ((procedure is int p && !Enum.IsDefined(typeof(NurseProcedureState.Procedure), p)) || (injury is int i && !Enum.IsDefined(typeof(InjuryType), i)))
        {
            return null;
        }

        Talent? disclosed = null;
        if (!talent.IsNone && (disclosed = Resolve(talent)?.TryCast<Talent>()) == null)
        {
            return null;
        }

        return NurseCards.Describe(procedure, disclosed, injury, injuryGames);
    }

    private ScriptableObject? Resolve(ChoiceObjectRef target) => target.Slot switch
    {
        ChoiceObjectSlot.Object1 => _object1,
        ChoiceObjectSlot.Object2 => _object2,
        ChoiceObjectSlot.AgentTalent => _agent?.powerups is { } talents && target.Index < talents.Count ? talents[target.Index] : null,
        ChoiceObjectSlot.AgentAbility => _agent?.ability,
        _ => null,
    };
}

/// <summary>
/// The Nurse's card descriptions (the game's static <c>NurseProcedureScreen.GetCardDescription(Nullable&lt;Procedure&gt;,
/// Talent, Nullable&lt;InjuryType&gt;, int)</c>) and its event's injury offer. Both <c>Nullable</c>s are 8-byte
/// structs: the interop's wrapper passes the inside of a boxed <c>Nullable</c>, which IL2CPP boxes as the bare enum, so the
/// method is called through <c>il2cpp_runtime_invoke</c> with buffers of the mod's own: each
/// <c>Nullable</c> its 8 bytes {hasValue, 3 bytes of padding, the value} (verified in the game's code: the method reads the flag
/// from the low byte and the value from the next 4 of its register argument), the talent its object's pointer, the games a pointer
/// to an int.
/// </summary>
internal static class NurseCards
{
    // A Nullable<enum>: the flag, then the value 4 bytes on (verified in the game's code: GetCardDescription; the state's own Nullable fields sit 4 bytes apart).
    private const int NullableValueOffset = 4;

    private static IntPtr _method;
    private static bool _looked;
    private static int _offerOffset = -1;
    private static bool _offerLooked;

    /// <summary>The card's description in this PC's language, or null when the method isn't there or throws.</summary>
    public static string? Describe(int? procedure, Talent? talent, int? injury, int injuryGames)
    {
        IntPtr method = Method();
        if (method == IntPtr.Zero)
        {
            return null;
        }

        // Three buffers in one: the procedure's Nullable (0), the injury's (8), the games (16).
        IntPtr buffers = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt64(buffers, 0);
            Marshal.WriteInt64(buffers, 8, 0);
            Marshal.WriteInt64(buffers, 16, 0);
            if (procedure is int p)
            {
                Marshal.WriteByte(buffers, 0, 1);
                Marshal.WriteInt32(buffers, NullableValueOffset, p);
            }

            if (injury is int i)
            {
                Marshal.WriteByte(buffers, 8, 1);
                Marshal.WriteInt32(buffers, 8 + NullableValueOffset, i);
            }

            Marshal.WriteInt32(buffers, 16, injuryGames);
            nint[] args = { buffers, talent?.Pointer ?? IntPtr.Zero, buffers + 8, buffers + 16 };
            nint result = Il2CppApi.RuntimeInvoke(method, IntPtr.Zero, args, out nint exception);
            GC.KeepAlive(talent);
            return exception != IntPtr.Zero || result == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffers);
        }
    }

    /// <summary>
    /// Host: the Nurse event's injury offer (its roster's <c>InjuryOfferType</c>, read in place: the interop's getter misreads the boxed
    /// <c>Nullable</c>, and <c>InjuryOfferGames</c>), or null when it has no roster or the field isn't found.
    /// </summary>
    public static NurseOffer? Offer(EventNode node)
    {
        NurseProcedureRoster? roster = node.TryCast<NurseEvent>()?._roster;
        int offset = OfferOffset();
        if (roster == null || offset < 0)
        {
            return null;
        }

        int? injury = Marshal.ReadByte(roster.Pointer, offset) != 0 ? Marshal.ReadInt32(roster.Pointer, offset + NullableValueOffset) : null;
        return new NurseOffer(injury, roster.InjuryOfferGames);
    }

    /// <summary>The method (looked up once by name and parameter count; one overload in this build), or zero (logged once).</summary>
    private static IntPtr Method()
    {
        if (!_looked)
        {
            _looked = true;
            IntPtr type = Il2CppClassPointerStore<NurseProcedureScreen>.NativeClassPtr;
            _method = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(type, "GetCardDescription", 4);
            if (_method == IntPtr.Zero)
            {
                ModLog.Warn("Menus: the Nurse's card description method wasn't found in this game; a friend's Nurse cards show the host's texts.");
            }
        }

        return _method;
    }

    /// <summary>The roster's injury offer field's offset (from the game's class, inside the object), or -1 (logged once).</summary>
    private static int OfferOffset()
    {
        if (!_offerLooked)
        {
            _offerLooked = true;
            IntPtr type = Il2CppClassPointerStore<NurseProcedureRoster>.NativeClassPtr;
            IntPtr field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, "InjuryOfferType");
            int offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);

            // Past the object header, its 8 bytes inside the roster (verified in the game's code).
            _offerOffset = offset is >= 0x10 and <= 0x200 ? offset : -1;
            if (_offerOffset < 0)
            {
                ModLog.Warn($"Menus: the Nurse roster's injury offer field wasn't found in this game (offset {offset}); its injury cards go to friends in the host's language.");
            }
        }

        return _offerOffset;
    }
}
#endif
