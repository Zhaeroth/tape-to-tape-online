#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppI2.Loc;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// What <see cref="LobbyLocalization"/> adds to the game's I2 source: <see cref="LobbyWordTable"/>, over <c>T2TOnline.Core.Lobby.LobbyWords</c>.
/// </summary>
internal interface ILobbyWordTable
{
    /// <summary>Every term, "t2t/..." .</summary>
    IEnumerable<string> Terms { get; }

    /// <summary>The term's text in a language column (I2's language name), or null to leave it blank (I2 falls back).</summary>
    string? Text(string term, string languageName);
}

/// <summary>The real table: every word of <see cref="LobbyWords"/>, by the game's language names.</summary>
internal sealed class LobbyWordTable : ILobbyWordTable
{
    /// <inheritdoc/>
    public IEnumerable<string> Terms => LobbyWords.All.Select(word => word.Key);

    /// <inheritdoc/>
    public string? Text(string term, string languageName)
    {
        int column = LobbyLocalization.LanguageIndexOf(languageName);
        return column < 0 ? null : LobbyWords.Find(term)?.Texts[column];
    }
}

/// <summary>
/// The native lobby's words in the game's own I2 source: <c>LanguageSourceData.AddTerm</c>
/// is plain and in memory (nothing reaches disk; a second call returns the term), so cloned <c>Localize</c> components show them and follow
/// language changes with no mod code (<c>LocalizeAll</c> finds every <c>Localize</c>, inactive ones too). The <c>t2t/</c> prefix keeps them from the
/// game's prefix lookups; <see cref="Campaign.Menus.PromptViews"/> skips it when it maps prompt texts back to terms. If the source can't be
/// used, labels get the words as plain text (they don't follow a language change then; the log says so).
/// </summary>
internal static class LobbyLocalization
{
    /// <summary>The term prefix.</summary>
    public const string Prefix = "t2t/";

    /// <summary>The title's row.</summary>
    public const string Online = "t2t/online";

    /// <summary>The group's first row.</summary>
    public const string HostLobby = "t2t/host_lobby";

    /// <summary>The group's second row.</summary>
    public const string JoinLobby = "t2t/join_lobby";

    private static bool _tried;

    /// <summary>The words added to the game's source.</summary>
    public static ILobbyWordTable Words { get; set; } = new LobbyWordTable();

    /// <summary>True when the words are in the game's source.</summary>
    public static bool Installed { get; private set; }

    /// <summary>True for one of the mod's terms.</summary>
    public static bool IsOurs(string? term) => term != null && term.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Adds the words to the game's source, once a game. Never throws; a failure is logged once and labels fall back to text.</summary>
    public static void Install()
    {
        if (_tried)
        {
            return;
        }

        _tried = true;
        try
        {
            LocalizationManager.InitializeIfNeeded();
            Il2CppSystem.Collections.Generic.List<LanguageSourceData>? sources = LocalizationManager.Sources;
            LanguageSourceData? source = sources != null && sources.Count > 0 ? sources[0] : null;
            Il2CppSystem.Collections.Generic.List<LanguageData>? languages = source?.mLanguages;
            if (source == null || languages == null || languages.Count == 0)
            {
                ModLog.Warn("Lobby words: the game's translation source isn't there; labels show the words as plain text and don't follow a language change.");
                return;
            }

            int terms = 0;
            int texts = 0;
            int blanks = 0;
            foreach (string key in Words.Terms)
            {
                TermData? term = source.AddTerm(key, eTermType.Text, false);
                Il2CppStringArray? column = term?.Languages;
                if (term == null || column == null)
                {
                    throw new InvalidOperationException($"AddTerm gave nothing for {key}");
                }

                terms++;
                for (int i = 0; i < languages.Count && i < column.Length; i++)
                {
                    string? text = Words.Text(key, languages[i].Name);
                    if (string.IsNullOrEmpty(text))
                    {
                        blanks++; // a language added by a game patch stays blank, and I2 falls back
                    }
                    else
                    {
                        // The term's column is the game's (AddTerm makes it, already in the source) and each string is fresh,
                        // held only by it: stored through the write barrier (GameArrays), so the incremental collector can't
                        // miss one the column was marked without.
                        Campaign.Menus.GameArrays.Store(column, i, Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(text));
                        texts++;
                    }
                }
            }

            Installed = true;
            ModLog.Info($"Lobby words: {terms} terms in {SafeText.Clean(source.ownerObject?.name, 32)} ({languages.Count} languages, {texts} texts, {blanks} blank).");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby words: adding them to the game's source failed ({ModLog.SafeErrorText(ex)}); labels show the words as plain text and don't follow a language change.");
        }
    }

    /// <summary>Points a cloned label at <paramref name="term"/>: through its <c>Localize</c> when the words are in the game's source, else as plain text.</summary>
    public static void Apply(TMP_Text label, string term)
    {
        Localize? localize = label.GetComponent<Localize>();
        if (Installed && localize != null)
        {
            localize.SetTerm(term);
            return;
        }

        if (localize != null)
        {
            UiClone.Destroy(localize);
        }

        label.text = Words.Text(term, LocalizationManager.CurrentLanguage ?? "English") ?? Words.Text(term, "English") ?? GameWord(term) ?? term;
    }

    /// <summary>A language's place in <see cref="LobbyWords.Languages"/>, or -1, by the one rule (<see cref="GameLanguages.IndexOf"/>).</summary>
    public static int LanguageIndexOf(string? languageName) => GameLanguages.IndexOf(languageName);

    /// <summary>This PC's language, for <see cref="LobbyLine.Text"/>: English when the game's is one the words don't know.</summary>
    public static int Language() => Math.Max(LanguageIndexOf(LocalizationManager.CurrentLanguage), 0);

    /// <summary>The game's own text for one of its terms, in this PC's language, or null.</summary>
    public static string? GameWord(string term)
    {
        string? text = LocalizationManager.GetTranslation(term, true, 0, true, false, null, null, true);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>A line in this PC's language: a word of the mod's, or a game's term, with its arguments.</summary>
    public static string Say(LobbyLine line) => line.Text(Language(), GameWord);

    /// <summary>A term alone in this PC's language.</summary>
    public static string Say(string term) => new LobbyLine(term, Array.Empty<string>()).Text(Language(), GameWord);
}
#endif
