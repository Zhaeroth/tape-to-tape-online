#if HAS_INTEROP
using System;
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// One yes/no from the session (a held Steam invite: "Leave Alex's lobby and join Sam's?"), on the game's own <c>CustomizableYesNoPrompt</c>, as <c>PromptViews</c> opens it: the first
/// button is the safe one, so B and a mashed A keep things as they are. The answer is read from the prompt's own result. Anything else ends the question as "stay": the prompt not
/// opening, its going away, the time running out. Never throws.
/// </summary>
internal sealed class LobbyQuestion
{
    private const double OpenSeconds = 5;
    private const double AnswerSeconds = 180;

    private readonly string _title;
    private readonly Action<bool> _answered;
    private readonly double _since = FrameClock.Seconds;
    private CustomizableYesNoPrompt? _prompt;

    private LobbyQuestion(string title, Action<bool> answered)
    {
        _title = title;
        _answered = answered;
    }

    /// <summary>The question up now, or null.</summary>
    public static LobbyQuestion? Current { get; private set; }

    /// <summary>Asks, if nothing else is being asked. <paramref name="answered"/> gets true for the second button (the one that acts) and false for anything else, once.</summary>
    public static bool Ask(string title, string text, string stay, string go, Action<bool> answered)
    {
        ArgumentNullException.ThrowIfNull(answered);
        if (Current != null)
        {
            return false;
        }

        try
        {
            UIModule ui = MenuGame.Module() ?? throw new InvalidOperationException("the game's UI module isn't there");
            string id = MenuDefinitionCatalogReference.CustomizableYesNoPrompt.Name;
            var data = new CustomizableYesNoPrompt.Data(title, text, stay, go, false, new Il2CppSystem.Nullable<PromptTemplateMenu.PromptTemplateResult>(), false);
            _ = ui.ShowMenuAsync<CustomizableYesNoPrompt>(id, data);
            Current = new LobbyQuestion(title, answered);

            // Not its words: they name players by their Steam names, and whoever asks has logged what it's about by tag.
            ModLog.Info($"Lobby: asking on the game's yes/no ({text.Length} characters).");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: couldn't ask ({ModLog.SafeErrorText(ex)}); that means no.");
            return false;
        }
    }

    /// <summary>Every frame: the prompt found, its answer read.</summary>
    public static void Tick()
    {
        LobbyQuestion? question = Current;
        if (question == null)
        {
            return;
        }

        try
        {
            question.Step();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: the question failed ({ModLog.SafeErrorText(ex)}); that means no.");
            question.End(false);
        }
    }

    /// <summary>The session is going: whatever's being asked is over, unanswered.</summary>
    public static void Cancel(string why)
    {
        if (Current is { } question)
        {
            ModLog.Info($"Lobby: the question is off ({why}).");
            Current = null;
            try
            {
                // The held object first, then whether it was collected, and only then Unity's own word (its equality calls the engine with the object).
                if (question._prompt is not null && !question._prompt.WasCollected && question._prompt != null)
                {
                    _ = question._prompt.Hide();
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Lobby: closing the question failed ({ex.GetType().Name}).");
            }
        }
    }

    private void Step()
    {
        double waited = FrameClock.Seconds - _since;

        // Not found yet: the held object itself is asked, never Unity (a prompt the game has destroyed equals null to it, and isn't looked for again).
        if (_prompt is null)
        {
            if (LobbyShell.TopShown()?.TryCast<CustomizableYesNoPrompt>() is { } top && top.m_TitleText?.text == _title)
            {
                _prompt = top;
            }
            else if (waited > OpenSeconds)
            {
                ModLog.Warn($"Lobby: the question's prompt didn't open within {OpenSeconds:0} s; that means no.");
                End(false);
            }

            return;
        }

        // First, and whatever Unity says of the menu by now: the game sends the answer as the close ends, and the menu may be going in that very frame (verified in the game's code:
        // CloseWithResult); a prompt the game closes by itself has its Cancel as it's destroyed (BehaviourDestroy). The result is read from the held object, which outlives its Unity half.
        if (!_prompt.WasCollected && _prompt.m_HasSentResult)
        {
            End(_prompt.m_Result == PromptTemplateMenu.PromptTemplateResult.No);
            return;
        }

        if (_prompt.WasCollected || _prompt == null)
        {
            End(false);
        }
        else if (waited > AnswerSeconds)
        {
            _ = _prompt.Hide();
            End(false);
        }
    }

    private void End(bool go)
    {
        Current = null;
        ModLog.Info($"Lobby: the question's answer: {(go ? "go" : "stay")}.");
        _answered(go);
    }
}
#endif
