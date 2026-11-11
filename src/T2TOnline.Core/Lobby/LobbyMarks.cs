using System;

namespace T2TOnline.Core.Lobby;

/// <summary>An icon that goes with a word, so that colour is never the only mark (XAG 103). The kit maps each to one of the game's sprites, with a fallback chain ending in the word alone.</summary>
public enum LobbyIcon
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>A plus: an empty seat that can be filled (<c>icon-plus</c>).</summary>
    Plus,

    /// <summary>An invite out (<c>icon-checkmark</c>).</summary>
    Invited,

    /// <summary>Something in progress (<c>Circle Filled - x64</c>, the loading dots).</summary>
    Dots,

    /// <summary>Here: their device's icon (<c>icon-controller-*</c>, <c>icon-keyboard-*</c>).</summary>
    Device,

    /// <summary>A kept seat's clock (nothing in the game looks like one: <c>icon-warning</c>).</summary>
    Clock,

    /// <summary>The host's mark (<c>crown</c>).</summary>
    Crown,

    /// <summary>The ready mark (<c>icon-ready</c>).</summary>
    Ready,

    /// <summary>Can't be chosen, and it says why (<c>icon-lock</c>).</summary>
    Lock,

    /// <summary>Invited you (<c>icon-tooltip</c>).</summary>
    InvitedYou,
}

/// <summary>A state's icon and word together.</summary>
/// <param name="Icon">The icon.</param>
/// <param name="Line">The word and its arguments.</param>
public readonly record struct LobbyMark(LobbyIcon Icon, LobbyLine Line);

/// <summary>The icon and word of each state a seat or a row can be in: never a colour alone.</summary>
public static class LobbyMarks
{
    /// <summary>A kept seat's clock as it reads on a card: "0:27".</summary>
    public static string Clock(int seconds)
    {
        int left = Math.Max(0, seconds);
        return $"{left / 60}:{left % 60:00}";
    }

    /// <summary>A seat's mark. An empty seat reads "Open seat" here; a host's own prompt on it says Invite.</summary>
    public static LobbyMark Of(SeatStage stage, int reconnectSeconds = 0) => stage switch
    {
        SeatStage.Invited => new LobbyMark(LobbyIcon.Invited, new LobbyLine(LobbyWords.Invited)),
        SeatStage.Joining => new LobbyMark(LobbyIcon.Dots, new LobbyLine(LobbyWords.Joining)),
        SeatStage.Here => new LobbyMark(LobbyIcon.Device, new LobbyLine(LobbyWords.Here)),
        SeatStage.Loading => new LobbyMark(LobbyIcon.Dots, LobbyLine.Game(LobbyTerms.Loading)),
        SeatStage.Reconnecting => new LobbyMark(LobbyIcon.Clock, new LobbyLine(LobbyWords.Reconnecting, Clock(reconnectSeconds))),
        _ => new LobbyMark(LobbyIcon.Plus, new LobbyLine(LobbyWords.OpenSeat)),
    };

    /// <summary>The host's mark: the crown and the word.</summary>
    public static LobbyMark Host => new(LobbyIcon.Crown, new LobbyLine(LobbyWords.Host));

    /// <summary>The ready mark's two states: the game's word and tick, or the mod's word alone.</summary>
    public static LobbyMark Ready(bool ready) =>
        ready ? new LobbyMark(LobbyIcon.Ready, LobbyLine.Game(LobbyTerms.Ready)) : new LobbyMark(LobbyIcon.None, new LobbyLine(LobbyWords.NotReady));

    /// <summary>The ribbon's words after a game; null when there's nothing measured to show.</summary>
    public static LobbyLine? Ribbon(in LobbyResult result)
    {
        string first = LobbyWords.Number(result.First);
        string second = LobbyWords.Number(result.Second);
        return result.Kind switch
        {
            LobbyResultKind.MatchWon => new LobbyLine(LobbyWords.ResultWon, first, second),
            LobbyResultKind.MatchLost => new LobbyLine(LobbyWords.ResultLost, first, second),
            LobbyResultKind.MatchTied => new LobbyLine(LobbyWords.ResultTied, first, second),
            LobbyResultKind.MatchScore => new LobbyLine(LobbyWords.ResultScore, first, second),
            LobbyResultKind.RunWon => new LobbyLine(LobbyWords.ResultRunWon, first, second),
            LobbyResultKind.RunOver => new LobbyLine(LobbyWords.ResultRunOver, first, second),
            LobbyResultKind.RunSaved => new LobbyLine(LobbyWords.ResultRunSaved, first, second),
            _ => null,
        };
    }

    /// <summary>What the status line says about the host, for friends; null when there's nothing to say. Names the host from <paramref name="hostName"/>, cleaned.</summary>
    public static LobbyLine? HostStatus(HostActivity activity, string hostName) => activity switch
    {
        HostActivity.PickingSave => new LobbyLine(LobbyWords.StatusPickingSave, hostName),
        HostActivity.PickingMode => new LobbyLine(LobbyWords.StatusPickingMode, hostName),
        HostActivity.Inviting => new LobbyLine(LobbyWords.StatusInviting, hostName),
        HostActivity.WaitingForEveryone => new LobbyLine(LobbyWords.StatusWaiting, hostName),
        _ => null,
    };
}
