#if HAS_INTEROP
using System;
using Il2Cpp;
using Il2CppFighting;
using Il2CppState;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// Brawl offers for friends, as for couch players (decided: couch parity). The game accepts an offer only from what it
/// reads directly in <c>GameControllerBase.CheckFightComboInput</c>: the host PC's keyboard (Q+E) and its physical
/// joysticks (LB+RB), so no friend could accept one online. The host watches each friend's played input for the same
/// combo on their own device (<see cref="Core.Input.BrawlCombo"/>) and accepts for them the way the game does, under the
/// game's own conditions: an offer open, the game state not GoalScored, no fight on in this match.
/// </summary>
internal static class BrawlComboRelay
{
    private const int NotTakenLogged = 3;

    private static int _failures;
    private static int _notTaken;
    private static int _combos;
    private static int _accepts;
    private static int _noOffer;

    /// <summary>
    /// Every friend's combo that came to <see cref="TryAccept"/> in this game's life, the ones that accepted an offer and the
    /// ones refused with no offer open (never started over: the check tour reads them before a press and after it).
    /// </summary>
    public static BrawlRelayCounts Counts => new(_combos, _accepts, _noOffer);

    /// <summary>The count of combos not taken, for the hosting's end, and starts it over.</summary>
    public static int TakeNotTaken()
    {
        int count = _notTaken;
        _notTaken = 0;
        return count;
    }

    /// <summary>
    /// A friend just pressed the combo: accepts the offer if one is open and the game would take the combo now. Returns
    /// whether it accepted, logging who did as <paramref name="seat"/>: the friend's tag and seat, never a Steam name (D127).
    /// Never throws: after three failures the relay is off for the session.
    /// </summary>
    public static bool TryAccept(string seat, string combo)
    {
        _combos++;
        if (_failures >= 3)
        {
            return false;
        }

        try
        {
            // CheckFightComboInput's conditions: not fighting, an offer open, not after a goal (GameState GoalScored). The
            // game reads "fighting" from the match's own controller, a flag it sets together with the match's Fight state
            // when a fight starts: that state stands for it here. The brawl HUD's flag doesn't: the game leaves it set when
            // a match ends during a fight, and with it every friend's combo in the matches after would be refused for a
            // fight long over, where the game's own check would take a couch player's.
            bool fighting = HudModel.CurrentGameState() == (int)GameState.Fight;
            bool offer = BrawlChallengeSystem.IsOfferActive;
            if (fighting || !offer || GameManager.GameState == GameState.GoalScored)
            {
                _noOffer += offer ? 0 : 1;

                // Said for the first few, so "no Brawl line" can't hide a combo that came while the game wouldn't take one.
                if (++_notTaken <= NotTakenLogged)
                {
                    string why = fighting ? "a brawl is under way" : !offer ? "no offer open" : "after a goal";
                    ModLog.Info($"Brawl: {seat} pressed {combo}, but the game wouldn't take it now ({why}); not accepted" +
                        $"{(_notTaken == NotTakenLogged ? ". Further ones are counted" : string.Empty)}.");
                }

                return false;
            }

            BrawlChallengeSystem.AcceptOffer();
            _accepts++;
            ModLog.Info($"Brawl: {seat} accepted the offer ({combo}), as a couch player can.");
            return true;
        }
        catch (Exception ex)
        {
            _failures++;
            ModLog.Error($"Brawl: accepting the offer for {seat} failed{(_failures >= 3 ? "; friends' brawl combos are off for this session" : string.Empty)}", ex);
            return false;
        }
    }
}
#endif
