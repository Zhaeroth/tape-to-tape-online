#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppBishopGames.UI;
using Il2CppI2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using PromptResult = Il2CppTape2Tape.Customization.UI.PromptTemplateMenu.PromptTemplateResult;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The game's prompts as real views on a friend (v1):
/// <list type="bullet">
/// <item>The two customizable prompts (<c>CustomizableYesNoPrompt</c>, <c>CustomizableDismissablePrompt</c>): the host reads
/// the prompt's data at its <c>InitData</c> and sends it as a <see cref="MenuAttachmentKind.Prompt"/> attachment (format 2, v1),
/// each text with the exact term the game made it from: every way the game makes a prompt (the callers' literal
/// keys, the open menus' own <c>LocalizedString</c> fields; <see cref="PromptProvenance"/>) is made as the game made it and
/// compared with the prompt's texts; only when none gives them all is each text's term picked from the game's terms with that
/// text in the host's language (<see cref="PromptTerms"/>, built once per language), and a text with none goes as the host's
/// text. A friend makes each text with the same call in its own language (<see cref="Make"/>), then the same data with the
/// game's constructor, and opens the real prompt with it.</item>
/// <item>The fixed campaign prompts (the lineup's and the benchwarmers' invalid-roster notices, the benchwarmers'
/// explanation): no data; a friend opens them by id with their class, and binds them at their <c>Show</c> (they have no
/// <c>InitData</c> of their own).</item>
/// </list>
/// The host's highlight follows by path; its click shows as a press; the friend's own clicks are refused
/// (<c>OnYesClick</c>/<c>OnNoClick</c>/<c>OnCancelClick</c>, <c>PerformDismissal</c>: backstops to the friend's input lock);
/// the prompt closes with the host's (the base close). The yes/no data constructor itself can't be patched: its
/// <c>Nullable&lt;PromptTemplateResult&gt;</c> is passed by value (8 bytes), and Il2CppInterop's detour copies a
/// <c>Nullable</c> argument from the address the argument holds. A text the game formats from a term and arguments (the
/// customization menus' "can't delete this skater") goes as a recipe a friend's game makes in its own language
/// (PromptViews.Formats.cs).
/// </summary>
internal static partial class PromptViews
{
    /// <summary>The yes/no prompt's class.</summary>
    public const string YesNo = "CustomizableYesNoPrompt";

    /// <summary>The notice's class.</summary>
    public const string Dismissable = "CustomizableDismissablePrompt";

    /// <summary>The lineup's invalid-roster notice (menu "LineupInvalidRosterPrompt").</summary>
    public const string LineupInvalid = "LineupRosterInvalidPrompt";

    /// <summary>The benchwarmers' invalid-roster notice (menu "BenchwarmerInvalidRosterPrompt").</summary>
    public const string BenchwarmerInvalid = "BenchwarmerRosterInvalidPrompt";

    /// <summary>The benchwarmers' explanation (menu "BenchwarmerExplanationPrompt").</summary>
    public const string BenchwarmerExplanation = "BenchwarmerExplanationPrompt";

    private const int TextChars = 48;

    // DefaultSelection's place in the yes/no data, and its value's place in the Nullable (hasValue first; verified in the game's
    // code: InitData reads both).
    private const int NullableValueOffset = 4;

    private static PromptTerms? _terms;
    private static string? _termsFailed;
    private static int _selectionOffset = -1;
    private static bool _selectionLooked;

    /// <summary>The prompt classes shown for real.</summary>
    public static readonly string[] Types = { YesNo, Dismissable, LineupInvalid, BenchwarmerInvalid, BenchwarmerExplanation };

    /// <summary>True for a prompt class shown for real.</summary>
    public static bool IsPrompt(string type) => Array.IndexOf(Types, type) >= 0;

    /// <summary>True for a prompt made from the host's data (an attachment comes with it).</summary>
    public static bool NeedsData(string type) => type is YesNo or Dismissable;

    /// <summary>True for a prompt with no <c>InitData</c> of its own: a friend binds its copy at its <c>Show</c>.</summary>
    public static bool BindsAtShow(string type) => type is LineupInvalid or BenchwarmerInvalid or BenchwarmerExplanation;

    /// <summary>The game's open of prompt <paramref name="id"/> with its concrete class (the generic is called, never patched).</summary>
    public static bool Show(UIModule ui, string type, string id, Il2CppSystem.Object request)
    {
        switch (type)
        {
            case YesNo:
                _ = ui.ShowMenuAsync<CustomizableYesNoPrompt>(id, request);
                return true;
            case Dismissable:
                _ = ui.ShowMenuAsync<CustomizableDismissablePrompt>(id, request);
                return true;
            case LineupInvalid:
                _ = ui.ShowMenuAsync<Il2CppTape2Tape.Hockey.UI.LineupRosterInvalidPrompt>(id, request);
                return true;
            case BenchwarmerInvalid:
                _ = ui.ShowMenuAsync<Il2CppTape2Tape.Hockey.UI.BenchwarmerRosterInvalidPrompt>(id, request);
                return true;
            case BenchwarmerExplanation:
                _ = ui.ShowMenuAsync<Il2CppTape2Tape.Hockey.UI.BenchwarmerExplanationPrompt>(id, request);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Host: a customizable prompt got its data (its <c>InitData</c>): the data goes with the menu for friends, as terms when
    /// it can. Never throws (the patch guards it too); one that can't be read goes to friends as text.
    /// </summary>
    public static void HostPrompt(HostMenus host, AbstractMenu menu, Il2CppSystem.Object? data, bool friends)
    {
        string type = MenuGame.TypeName(menu);
        PromptData? prompt;
        string how;
        string title = string.Empty;
        try
        {
            prompt = Read(host, menu, type, data, out how, out title);
        }
        catch (Exception ex)
        {
            prompt = null;
            how = $"{ModLog.SafeErrorText(ex)}";
        }

        if (prompt == null)
        {
            ModLog.Warn($"Menus: the host's {type} couldn't be read ({how}); friends see it as text.");
            return;
        }

        if (!host.Attach(menu, MenuAttachmentKind.Prompt, serial => MenuAttachment.Prompt(serial, prompt), $"{ShownTextLog.Prompt(prompt, LogNames.TextShape)}, {how}", friends, out string problem))
        {
            ModLog.Warn($"Menus: the host's {type} can't be sent ({problem}); friends see it as text.");
            return;
        }

        ModLog.Info($"Menus: the host's {type} (title {ShownTextLog.Shown(prompt.Title, title, LogNames.TextShape)}) goes to friends {how}.");
    }

    /// <summary>The host's prompt data as <see cref="PromptData"/>, each text with how it was made (<see cref="Resolve"/>); null with why.</summary>
    private static PromptData? Read(HostMenus host, AbstractMenu menu, string type, Il2CppSystem.Object? data, out string how, out string title)
    {
        how = string.Empty;
        title = string.Empty;
        if (type == YesNo && data?.TryCast<CustomizableYesNoPrompt.Data>() is { } yesNo)
        {
            string?[] texts = { yesNo.Title, yesNo.Desc, yesNo.Yes, yesNo.No };
            title = texts[0] ?? string.Empty;
            PromptText[] sent = Resolve(host, menu, PromptKind.YesNo, texts, out how);
            int? selection = DefaultSelection(yesNo);
            return Checked(new PromptData
            {
                Kind = PromptKind.YesNo,
                Title = sent[0],
                Description = sent[1],
                Yes = sent[2],
                No = sent[3],
                ShowCloseButton = yesNo.ShowCloseButton,
                DefaultSelection = selection is >= 0 and <= PromptData.MaxSelection ? selection : null,
            }, ref how);
        }

        if (type == Dismissable && data?.TryCast<CustomizableDismissablePrompt.Data>() is { } notice)
        {
            string?[] texts = { notice.Title, notice.Desc, notice.Dismiss };
            title = texts[0] ?? string.Empty;
            PromptText[] sent = Resolve(host, menu, PromptKind.Dismissable, texts, out how);
            return Checked(new PromptData { Kind = PromptKind.Dismissable, Title = sent[0], Description = sent[1], Dismiss = sent[2] }, ref how);
        }

        how = data == null ? "it has no data" : $"its data is a {MenuGame.TypeName(data)}";
        return null;
    }

    private static PromptData? Checked(PromptData prompt, ref string how)
    {
        string? problem = prompt.Problem();
        if (problem == null)
        {
            return prompt;
        }

        how = problem;
        return null;
    }

    /// <summary>
    /// Each of the prompt's texts with how a friend's game makes it: the exact terms of the first way the game makes a prompt
    /// whose texts, made as the game made them, are this prompt's (the open menus' own, newest first, then the callers'
    /// literal keys); else per text, the game's term with that text in this language (the likeliest of several), or the text
    /// itself. <paramref name="how"/> says which, for the log.
    /// </summary>
    private static PromptText[] Resolve(HostMenus host, AbstractMenu prompt, PromptKind kind, string?[] texts, out string how)
    {
        string exactProblem = string.Empty;
        try
        {
            List<PromptSource> sources = PromptProvenance.Order(Openers(host, prompt));
            if (PromptProvenance.Match(kind, texts, sources, Make) is { } exact)
            {
                how = $"as the game's terms (exact: {exact}; each friend's game shows it in its own language)";
                return exact.Texts.ToArray();
            }
        }
        catch (Exception ex)
        {
            exactProblem = $"; its opener couldn't be read ({ModLog.SafeErrorText(ex)})";
        }

        PromptTerms? terms = Terms();
        PromptText[] made = PromptProvenance.Guess(kind, texts, terms, out PromptTextOrigin[] origins);
        string recipes = AsRecipes(kind, texts, made, origins);
        if (terms == null)
        {
            how = $"as the host's texts (the game's terms couldn't be read{exactProblem})";
        }
        else if (!made.Any(t => t.IsTerm || t.Kind == PromptTextKind.Recipe))
        {
            how = $"as the host's texts (none is one of the game's terms in {SafeText.Clean(terms.Language, 32)}{exactProblem}{recipes})";
        }
        else
        {
            string guessed = PromptProvenance.Describe(origins, kind);
            how = $"as the game's terms (no opener's own terms gave all of its texts{exactProblem}; " +
                $"{(guessed.Length > 0 ? guessed : "each text is its term's only one")}{recipes}; each friend's game shows the terms in its own language)";
        }

        return made;
    }

    /// <summary>
    /// The menus that may have opened the prompt, newest first, each with its own ways of making one (its
    /// <c>LocalizedString</c> fields, read now; the literal keys are <see cref="PromptProvenance"/>'s), then the title screen's
    /// (it isn't a menu).
    /// </summary>
    private static List<(string Opener, IReadOnlyList<PromptSource> Own)> Openers(HostMenus host, AbstractMenu prompt)
    {
        var openers = new List<(string, IReadOnlyList<PromptSource>)>();
        foreach (AbstractMenu menu in host.OpenMenus())
        {
            if (menu.Pointer != prompt.Pointer)
            {
                openers.Add((MenuGame.TypeName(menu), OwnSources(menu)));
            }
        }

        TitleScreen? title = UnityEngine.Object.FindObjectOfType<TitleScreen>();
        if (title != null && Fields(nameof(TitleScreen), "new run", title.m_ConfirmNewRunTitle, title.m_ConfirmNewRunDesc, title.m_ConfirmNewRunYes, title.m_ConfirmNewRunNo) is { } newRun)
        {
            openers.Add((nameof(TitleScreen), new[] { newRun }));
        }

        return openers;
    }

    /// <summary>
    /// A menu's own prompts made from its <c>LocalizedString</c> fields (verified in the game's code: each caller passes
    /// the four fields' <c>ToString</c> as title, description, yes and no); none for any other menu.
    /// </summary>
    private static PromptSource[] OwnSources(AbstractMenu menu)
    {
        PromptSource?[] own =
            menu.TryCast<EventChoiceMenu>() is { } choice ? new[] { Fields(nameof(EventChoiceMenu), "exit without a choice", choice.m_ConfirmExitTitle, choice.m_ConfirmExitDesc, choice.m_ConfirmExitYes, choice.m_ConfirmExitNo) }
            : menu.TryCast<RelicRewardMenu>() is { } relic ? new[] { Fields(nameof(RelicRewardMenu), "skip", relic.m_ConfirmExitTitle, relic.m_ConfirmExitDesc, relic.m_ConfirmExitYes, relic.m_ConfirmExitNo) }
            : menu.TryCast<SkaterPowerupRewardMenu>() is { } powerUp
                ? new[] { Fields(nameof(SkaterPowerupRewardMenu), "skip", powerUp.m_ConfirmExitTitle, powerUp.m_ConfirmExitDesc, powerUp.m_ConfirmExitYes, powerUp.m_ConfirmExitNo) }
            : menu.TryCast<WorldMapMenu>() is { } map ? new[] { Fields(nameof(WorldMapMenu), "concede", map.m_ConfirmConcedeTitle, map.m_ConfirmConcedeDesc, map.m_ConfirmConcedeYes, map.m_ConfirmConcedeNo) }
            : menu.TryCast<ControllerAssignmentMenu>() is { } controllers ? new[]
            {
                Fields(nameof(ControllerAssignmentMenu), "exit without saving", controllers.m_ExitWithoutSavingTitle, controllers.m_ExitWithoutSavingDesc,
                    controllers.m_ExitWithoutSavingYes, controllers.m_ExitWithoutSavingNo),
                Fields(nameof(ControllerAssignmentMenu), "AI vs AI", controllers.m_AIvsAIConfirmTitle, controllers.m_AIvsAIConfirmDesc, controllers.m_AIvsAIConfirmYes,
                    controllers.m_AIvsAIConfirmNo),
            }
            : Array.Empty<PromptSource?>();
        return own.Where(f => f != null).Select(f => f!).ToArray();
    }

    /// <summary>A yes/no prompt made from four <c>LocalizedString</c> fields; null when one can't go as it is (out of the wire's limits).</summary>
    private static PromptSource? Fields(string opener, string name, LocalizedString title, LocalizedString desc, LocalizedString yes, LocalizedString no)
    {
        PromptText[] texts = { Localized(title), Localized(desc), Localized(yes), Localized(no) };
        return texts.All(t => t.Problem("a text") == null) ? new PromptSource(opener, name, PromptKind.YesNo, texts) : null;
    }

    /// <summary>A <c>LocalizedString</c> field's term with its settings (empty for no term).</summary>
    private static PromptText Localized(LocalizedString field)
    {
        string? term = field.mTerm;
        return string.IsNullOrEmpty(term) ? PromptText.Empty
            : PromptText.Localized(term, field.mRTL_IgnoreArabicFix, field.mRTL_MaxLineLength, field.mRTL_ConvertNumbers, field.m_DontLocalizeParameters);
    }

    /// <summary>
    /// What a friend's copy of the host's prompt shows, for the log: "as the game's terms in English: "Skip?"",
    /// or the host's own texts, its first text as this PC made it (as its shape when it isn't one of the game's terms: it can
    /// hold a typed name, D127); a fixed prompt is the game's own. One line a prompt.
    /// </summary>
    public static string ShownHere(string type, MenuAttachment? attachment, string hostTag)
    {
        string language = SafeText.Clean(LocalizationManager.CurrentLanguage, 32);
        if (BindsAtShow(type) || attachment == null || !attachment.TryGetPrompt(out PromptData? prompt) || prompt == null)
        {
            return $"as the game's own fixed prompt (in {language})";
        }

        List<PromptText> texts = prompt.Texts.Where(t => t.Kind != PromptTextKind.Empty).ToList();
        int recipes = texts.Count(t => t.Kind == PromptTextKind.Recipe);
        int terms = texts.Count(t => t.IsTerm) + recipes;
        string made = recipes > 0 ? $"as the game's terms and {recipes} recipe(s)" : "as the game's terms";
        string how = terms == texts.Count ? $"{made} in {language}"
            : terms == 0 ? $"as {hostTag}'s texts"
            : $"{terms} of its {texts.Count} texts {made} in {language}, the rest as {hostTag}'s";
        string first = texts.Count > 0 ? ShownTextLog.Shown(texts[0], Make(texts[0]), LogNames.TextShape) : "no text";
        string selection = prompt.DefaultSelection is int wanted ? $"; default selection {(PromptResult)wanted}" : string.Empty;
        return $"{how}: {first}{selection}";
    }

    /// <summary>
    /// A prompt text made in this game's language as the host's game made it: a term with the data constructor's own
    /// translation (<see cref="PromptTextKind.Term"/>), or as <c>LocalizedString.ToString</c> makes it (its RTL settings and
    /// parameters, then the global parameters: <see cref="PromptTextKind.LocalizedTerm"/>); a literal as it is. "" for a term
    /// with no translation here.
    /// </summary>
    internal static string Make(PromptText text)
    {
        switch (text.Kind)
        {
            case PromptTextKind.Term:
                return LocalizationManager.GetTranslation(text.Value, true, 0, true, false, null, null, true) ?? string.Empty;
            case PromptTextKind.LocalizedTerm:
                string made = LocalizationManager.GetTranslation(text.Value, !text.IgnoreArabicFix, text.MaxLineLength, !text.ConvertNumbers, true, null, null, true) ?? string.Empty;
                LocalizationManager.ApplyLocalizationParams(ref made, !text.DontLocalizeParameters);
                return made ?? string.Empty;
            case PromptTextKind.Literal:
                return text.Value;
            case PromptTextKind.Recipe:
                return MakeRecipe(text);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// The game's translation term whose text in this PC's language is <paramref name="text"/> (the run toasts' texts,
    /// <see cref="Toasts"/>), or false: none, or the terms can't be read.
    /// </summary>
    internal static bool TryTerm(string? text, out string term)
    {
        term = string.Empty;
        return !string.IsNullOrEmpty(text) && Terms() is { } terms && terms.TryFind(text, out term);
    }

    /// <summary>
    /// The game's terms by their text in its current language, made once per language (a few thousand terms; its time is
    /// logged). Null when they can't be read (logged once per language).
    /// </summary>
    private static PromptTerms? Terms()
    {
        string language = LocalizationManager.CurrentLanguage ?? string.Empty;
        if (_terms != null && _terms.Language == language)
        {
            return _terms;
        }

        if (_termsFailed == language)
        {
            return null;
        }

        long start = Stopwatch.GetTimestamp();
        var terms = new PromptTerms(language);
        var templates = new TextTemplates(language);
        int seen = 0;
        try
        {
            Il2CppSystem.Collections.Generic.List<LanguageSourceData>? sources = LocalizationManager.Sources;
            for (int s = 0; sources != null && s < sources.Count && !terms.Full; s++)
            {
                LanguageSourceData? source = sources[s];
                Il2CppSystem.Collections.Generic.List<TermData>? all = source?.mTerms;
                int index = source == null ? -1 : source.GetLanguageIndex(language, true, true);
                for (int i = 0; all != null && index >= 0 && i < all.Count && !terms.Full; i++)
                {
                    TermData? term = all[i];
                    Il2CppStringArray? texts = term?.Languages;
                    if (term != null && texts != null && index < texts.Length && !NativeLobby.LobbyLocalization.IsOurs(term.Term))
                    {
                        seen++;
                        terms.Add(term.Term, texts[index]);
                        _ = templates.Add(term.Term, texts[index]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _termsFailed = language;
            ModLog.Warn($"Menus: the game's translation terms couldn't be read ({ModLog.SafeErrorText(ex)}); the host's prompts go to friends as its texts.");
            return null;
        }

        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: indexed {terms.Count} of the game's {seen} translation(s) in {SafeText.Clean(language, 32)} in {ms:0.0} ms ({terms.Shared} term(s) share their text with another; ") +
            $"{templates.Count - TextTemplates.CodeFormats.Count} template(s) with holes), to tell the terms of a host's prompt or toast no opener's own terms give " +
            "(a friend's game shows them in its own language).");
        _terms = terms;
        _templates = templates;
        return terms;
    }

    /// <summary>
    /// Friend: the data to open the host's prompt of class <paramref name="type"/> with (see <see cref="Build"/>), or null with
    /// why. Never throws: a failure is the reason.
    /// </summary>
    public static Il2CppSystem.Object? Request(string type, MenuAttachment? attachment, out string problem)
    {
        try
        {
            return Build(type, attachment, out problem);
        }
        catch (Exception ex)
        {
            problem = $"its data couldn't be made here ({ModLog.SafeErrorText(ex)})";
            return null;
        }
    }

    /// <summary>
    /// Friend: the data to open the host's prompt of class <paramref name="type"/> with: the same data as the host's (from its
    /// <paramref name="attachment"/>), made with the game's own constructor and read back; for a fixed prompt, a fresh object
    /// (it takes no data). Null with why.
    /// </summary>
    private static Il2CppSystem.Object? Build(string type, MenuAttachment? attachment, out string problem)
    {
        problem = string.Empty;
        if (BindsAtShow(type))
        {
            return new Il2CppSystem.Object();
        }

        if (attachment == null || !attachment.TryGetPrompt(out PromptData? prompt) || prompt == null)
        {
            problem = "the host's prompt data isn't here";
            return null;
        }

        if ((type == YesNo) != (prompt.Kind == PromptKind.YesNo))
        {
            problem = $"the host's prompt data is for a {prompt.Kind} prompt";
            return null;
        }

        // Each text made here as the host's game made it, in this game's language (a term with no translation here can't).
        IReadOnlyList<PromptText> texts = prompt.Texts;
        string[] made = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            made[i] = Make(texts[i]);
            if (texts[i].IsTerm && made[i].Length == 0)
            {
                problem = $"the term \"{SafeText.Clean(texts[i].Value, TextChars)}\" has no translation in this game";
                return null;
            }
        }

        return prompt.Kind == PromptKind.YesNo ? YesNoData(prompt, made, out problem) : NoticeData(made, out problem);
    }

    private static CustomizableYesNoPrompt.Data? YesNoData(PromptData prompt, string[] made, out string problem)
    {
        // No default selection for the constructor; the host's, if any, is written after. (The interop's "new Nullable" is
        // il2cpp_object_new of the Nullable class, which IL2CPP makes a zeroed box of the bare enum; verified in the game's
        // code: Object::New; the constructor's 8 bytes from it start with that 0, which reads as "no value".) The texts
        // are made already: the constructor keeps them as they are.
        var data = new CustomizableYesNoPrompt.Data(made[0], made[1], made[2], made[3], prompt.ShowCloseButton, new Il2CppSystem.Nullable<PromptResult>(), false);
        int? selection = prompt.DefaultSelection is int wanted && Enum.IsDefined(typeof(PromptResult), wanted) ? wanted : null;
        if (selection is int value && !SetDefaultSelection(data, value))
        {
            problem = "its default selection couldn't be set here";
            return null;
        }

        problem = ReadBack(new[] { data.Title, data.Desc, data.Yes, data.No }, made) ??
            (data.ShowCloseButton != prompt.ShowCloseButton ? "its close button didn't read back as sent"
            : DefaultSelection(data) != selection ? "its default selection didn't read back as sent"
            : string.Empty);
        return problem.Length == 0 ? data : null;
    }

    private static CustomizableDismissablePrompt.Data? NoticeData(string[] made, out string problem)
    {
        var data = new CustomizableDismissablePrompt.Data(made[0], made[1], made[2], false);
        problem = ReadBack(new[] { data.Title, data.Desc, data.Dismiss }, made) ?? string.Empty;
        return problem.Length == 0 ? data : null;
    }

    /// <summary>Why the made data doesn't hold the texts made for it (null when it does, exactly).</summary>
    private static string? ReadBack(string?[] held, string[] made)
    {
        for (int i = 0; i < held.Length; i++)
        {
            if ((held[i] ?? string.Empty) != made[i])
            {
                return $"its text {i + 1} didn't read back as made";
            }
        }

        return null;
    }

    /// <summary>The yes/no data's default selection (the game's <c>PromptTemplateResult</c> number), or null for none.</summary>
    private static int? DefaultSelection(CustomizableYesNoPrompt.Data data)
    {
        int offset = SelectionOffset();
        if (offset < 0)
        {
            return null;
        }

        return Marshal.ReadByte(data.Pointer, offset) != 0 ? Marshal.ReadInt32(data.Pointer, offset + NullableValueOffset) : null;
    }

    /// <summary>Writes a default selection into the yes/no data (its <c>Nullable</c> field, in place). False when it can't.</summary>
    private static bool SetDefaultSelection(CustomizableYesNoPrompt.Data data, int value)
    {
        int offset = SelectionOffset();
        if (offset < 0)
        {
            return false;
        }

        Marshal.WriteInt32(data.Pointer, offset + NullableValueOffset, value);
        Marshal.WriteByte(data.Pointer, offset, 1);
        return true;
    }

    /// <summary>The <c>DefaultSelection</c> field's offset in the yes/no data (from the game's own class), or -1.</summary>
    private static int SelectionOffset()
    {
        if (!_selectionLooked)
        {
            _selectionLooked = true;
            IntPtr type = Il2CppClassPointerStore<CustomizableYesNoPrompt.Data>.NativeClassPtr;
            IntPtr field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, "DefaultSelection");
            int offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);

            // Past the object header and the strings, with the Nullable's 8 bytes inside the object (verified in the game's code).
            _selectionOffset = offset is >= 0x10 and <= 0x100 ? offset : -1;
            if (_selectionOffset < 0)
            {
                ModLog.Warn($"Menus: the yes/no prompt's default selection field wasn't found in this game (offset {offset}); a prompt with one shows as text.");
            }
        }

        return _selectionOffset;
    }
}
#endif
