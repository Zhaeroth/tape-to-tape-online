#if HAS_INTEROP
using System;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using I2LocalizationManager = Il2CppI2.Loc.LocalizationManager;

namespace T2TOnline.Plugin.Online;

// The game's language while a session is up (v1). A friend changes it in their own game's Settings menu (System tab, the Language
// row; the game lets it work at the title screen only): the game loads the new language's fonts, then sets
// LocalizationManager.CurrentLanguage, which re-translates every Localize component, and the menu saves config.es3 (in the lobby
// nothing guards it; while following, the save guard lets that through while the menu is open: D88). Nothing is sent: every word the mod carries is a term or a recipe, which each
// game makes in its own language. What the mod keeps of the old language (the prompt and toast term indexes) is dropped. Apart
// from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    private readonly LanguageWatch _languageWatch = new();
    private bool _languageOff;

    /// <summary>
    /// Every frame while a session is up: the game's language read twice a second, a change followed (the mod's caches dropped,
    /// said in the log). Off for the rest of the game's life after a failure. Never throws.
    /// </summary>
    private void UpdateLanguage(long now)
    {
        if (_languageOff)
        {
            return;
        }

        if (_host == null && _friend == null)
        {
            if (_languageWatch.Current != null)
            {
                _languageWatch.Reset(); // the next session starts from the language it finds
            }

            return;
        }

        try
        {
            LanguageChange? change = _languageWatch.Poll(now, static () => I2LocalizationManager.CurrentLanguage);
            if (change != null)
            {
                LanguageChanged(change);
            }
        }
        catch (Exception ex)
        {
            _languageOff = true;
            ModLog.Error("Language: following this game's language failed; the mod's word lists are made again only at the next game start", ex);
        }
    }

    private void LanguageChanged(LanguageChange change)
    {
        PromptViews.LanguageChanged();
        string before = SafeText.Clean(change.From, 32);
        string after = SafeText.Clean(change.To, 32);
        // The save guard holds only while following (from BeginPlay to EndPlay): in the lobby a friend's game, and its config.es3, are their own.
        string saved = _settingsSaves != null
            ? "Your Settings menu is open, so the save guard lets config.es3 keep the choice."
            : _friend != null && _playOn
                ? "Your Settings menu isn't open, so the save guard refuses config.es3 while you follow: the choice lasts until the game closes."
                : "config.es3 is this game's own to write.";
        string told = _friend != null
            ? $"Nothing was sent to {(_solo ? "the host side" : HostTag())}: the mod carries terms and recipes, which every game makes in its own language."
            : "Nothing was sent to friends: the mod carries terms and recipes, which every game makes in its own language.";
        ModLog.Info($"Language: this game's language changed from {before} to {after} (change {change.Number}); the mod's term and template indexes of {before} are dropped and made again " +
            $"for {after} at the next prompt or toast. {told} {saved}");
    }
}
#endif
