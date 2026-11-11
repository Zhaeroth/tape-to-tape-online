using System;
using System.Text.RegularExpressions;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Lobby;

public class JoinFailuresTests
{
    [Theory]
    [InlineData(2, LobbyFailure.Closed)] // DoesntExist
    [InlineData(3, LobbyFailure.NotAllowed)] // NotAllowed
    [InlineData(4, LobbyFailure.Full)]
    [InlineData(5, LobbyFailure.Unreachable)] // Error
    [InlineData(6, LobbyFailure.NotAllowed)] // Banned
    [InlineData(7, LobbyFailure.Limited)]
    [InlineData(8, LobbyFailure.Unknown)] // ClanDisabled
    [InlineData(9, LobbyFailure.Limited)] // CommunityBan
    [InlineData(10, LobbyFailure.Blocked)] // MemberBlockedYou
    [InlineData(11, LobbyFailure.Blocked)] // YouBlockedMember
    [InlineData(15, LobbyFailure.RateLimited)]
    [InlineData(99, LobbyFailure.Unknown)] // a number Steam adds later
    [InlineData(0, LobbyFailure.Unknown)]
    [InlineData(-1, LobbyFailure.Unknown)]
    public void Steams_answers_map_to_failures(int response, LobbyFailure failure)
    {
        Assert.Equal(failure, JoinFailures.FromSteamEnter(response));
    }

    [Theory]
    [InlineData(RejectReason.None, LobbyFailure.Unknown)]
    [InlineData(RejectReason.ProtocolMismatch, LobbyFailure.ModVersion)]
    [InlineData(RejectReason.GameBuildMismatch, LobbyFailure.GameBuild)]
    [InlineData(RejectReason.SessionFull, LobbyFailure.Full)]
    [InlineData(RejectReason.NotInLobby, LobbyFailure.Unknown)]
    [InlineData(RejectReason.NoControllerSlot, LobbyFailure.NoController)]
    [InlineData(RejectReason.UnsupportedInput, LobbyFailure.UnsupportedInput)]
    [InlineData(RejectReason.HostUnavailable, LobbyFailure.HostOff)]
    [InlineData(RejectReason.NotAFriend, LobbyFailure.NotAFriend)]
    [InlineData(RejectReason.Removed, LobbyFailure.Removed)]
    [InlineData(RejectReason.FriendshipUnconfirmed, LobbyFailure.HostFriendsUnchecked)]
    [InlineData((RejectReason)200, LobbyFailure.Unknown)]
    public void The_hosts_refusals_map_to_failures(RejectReason reason, LobbyFailure failure)
    {
        Assert.Equal(failure, JoinFailures.FromReject(reason));
    }

    [Fact]
    public void Every_reject_reason_has_a_failure_and_a_host_toast()
    {
        foreach (RejectReason reason in Enum.GetValues<RejectReason>())
        {
            Assert.True(Enum.IsDefined(JoinFailures.FromReject(reason)));
            Assert.Contains("Sam", JoinFailures.HostToast(reason, "Sam").Text(0));
        }
    }

    [Theory]
    [InlineData(LeaveReason.HostClosed, true, LobbyFailure.HostClosed)]
    [InlineData(LeaveReason.HostClosed, false, LobbyFailure.HostClosed)]
    [InlineData(LeaveReason.Quit, true, LobbyFailure.HostClosed)] // a host that quits
    [InlineData(LeaveReason.Removed, true, LobbyFailure.Removed)]
    [InlineData(LeaveReason.TimedOut, true, LobbyFailure.HostLost)]
    [InlineData(LeaveReason.TimedOut, false, LobbyFailure.Unreachable)]
    [InlineData(LeaveReason.LeftLobby, true, LobbyFailure.HostLost)]
    [InlineData(LeaveReason.None, false, LobbyFailure.Unreachable)]
    [InlineData((LeaveReason)200, true, LobbyFailure.HostLost)]
    [InlineData((LeaveReason)200, false, LobbyFailure.Unknown)]
    public void How_a_session_ended_maps_to_a_failure(LeaveReason reason, bool everJoined, LobbyFailure failure)
    {
        Assert.Equal(failure, JoinFailures.FromLeave(reason, everJoined));
    }

    [Theory]
    [InlineData(LobbyFailure.Full, "Alex's lobby is full. Try again when a seat opens.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.Closed, "Alex's lobby has closed.", FailureButtons.Ok, FailureLanding.JoinListNearestRow)]
    [InlineData(LobbyFailure.Unreachable, "Couldn't reach Alex's lobby. Check Steam is online, then try again.", FailureButtons.TryAgainOrBack, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.NotAllowed, "Steam won't let you into Alex's lobby. Ask Alex for an invite.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.Blocked, "Steam blocked this join: someone in the lobby has blocked someone.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.Limited, "Steam limits this account, so it can't join lobbies.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.RateLimited, "Too many tries. Wait a minute, then try again.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.ModVersion, "You and Alex have different T2T Online versions. You both need the same one.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.GameBuild, "You and Alex are on different game updates. Let Steam update, restart, then join.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.NotAFriend, "Only Alex's Steam friends can join. Add Alex on Steam, then try again.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.HostOff, "Online play is off in Alex's game: T2T Online doesn't know their game update yet.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.NoController, "Alex's game couldn't add a controller for you. Ask Alex to reopen the lobby.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.UnsupportedInput, "Alex's T2T Online doesn't know your kind of controller. You both need the same version.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.Unknown, "Couldn't finish joining Alex's lobby. Try again, or ask for an invite.", FailureButtons.TryAgainOrBack, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.Removed, "Alex removed you from the lobby.", FailureButtons.Ok, FailureLanding.JoinListNearestRow)]
    [InlineData(LobbyFailure.HostClosed, "Alex closed the lobby.", FailureButtons.Ok, FailureLanding.JoinListNearestRow)]
    [InlineData(LobbyFailure.HostLost, "Lost the connection to Alex's game. Your own progress is safe.", FailureButtons.Ok, FailureLanding.JoinListNearestRow)]
    [InlineData(LobbyFailure.OnlineOff, "T2T Online doesn't know this game update yet, so Online is off. Campaign and Play Now still work.", FailureButtons.Ok, FailureLanding.Title)]
    [InlineData(LobbyFailure.OnlineBroken, "Online is off after an error. Restart the game to play online.", FailureButtons.Ok, FailureLanding.Title)]
    [InlineData(LobbyFailure.SteamOff, "Steam isn't connected. Connect, then try again.", FailureButtons.TryAgainOrBack, FailureLanding.Title)]
    [InlineData(LobbyFailure.SteamWaiting, "Still waiting for Steam. Try again in a few seconds.", FailureButtons.TryAgainOrBack, FailureLanding.Title)]
    [InlineData(LobbyFailure.RestoredBackup, "A restored save needs a game restart first. Restart, then come back.", FailureButtons.Ok, FailureLanding.Title)]
    [InlineData(LobbyFailure.ScreenFailed, "Online couldn't open. Restart the game, and if it happens again, make a problem report in Settings > Online.", FailureButtons.None, FailureLanding.Stay)]
    [InlineData(LobbyFailure.CreateFailed, "Couldn't create a lobby. Check Steam is online, then try again.", FailureButtons.TryAgainOrBack, FailureLanding.OnlineGroup)]
    [InlineData(LobbyFailure.FriendsUnchecked, "Steam couldn't check your friends list. Restart Steam and the game, then try again.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.HostFriendsUnchecked, "Alex's Steam couldn't check their friends list. Alex needs to restart Steam and the game.", FailureButtons.Ok, FailureLanding.JoinListSameRow)]
    [InlineData(LobbyFailure.RelayOnlyRefused, "Online is off: Steam wouldn't keep your connections relayed, so other players could see your IP address. To play online anyway, turn on Settings > Online > Direct Connections and restart the game; your address is then shared.", FailureButtons.Ok, FailureLanding.Title)]
    [InlineData(LobbyFailure.DirectLink, "Steam connected you straight to Alex's game, which shares your IP addresses, so you left. Restart Steam and the game, then try again.", FailureButtons.Ok, FailureLanding.JoinListNearestRow)]
    public void Every_failure_says_what_the_table_says(LobbyFailure failure, string english, FailureButtons buttons, FailureLanding landing)
    {
        FailureMessage message = JoinFailures.Message(failure, "Alex");

        Assert.Equal(failure, message.Reason);
        Assert.Equal(english, message.Line.Text(0));
        Assert.Equal(buttons, message.Buttons);
        Assert.Equal(landing, message.Landing);
    }

    [Fact]
    public void Every_failure_has_words_buttons_and_a_landing()
    {
        foreach (LobbyFailure failure in Enum.GetValues<LobbyFailure>())
        {
            FailureMessage message = JoinFailures.Message(failure, "Alex");
            Assert.Equal(failure, message.Reason);
            for (int language = 0; language < LobbyWords.LanguageCount; language++)
            {
                string text = message.Line.Text(language);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("{", text);
            }
        }
    }

    [Fact]
    public void A_failure_this_version_does_not_know_still_says_something_with_a_way_on()
    {
        FailureMessage message = JoinFailures.Message((LobbyFailure)999, "Alex");

        Assert.Equal(LobbyFailure.Unknown, message.Reason);
        Assert.Equal(FailureButtons.TryAgainOrBack, message.Buttons);
    }

    [Fact]
    public void A_version_mismatch_with_both_versions_says_them()
    {
        FailureMessage message = JoinFailures.Message(LobbyFailure.ModVersion, "Alex", "0.9", "1.0");

        Assert.Equal("You and Alex have different T2T Online versions (yours 0.9, Alex's 1.0). You both need the same one.", message.Line.Text(0));
        Assert.DoesNotContain("0.9", JoinFailures.Message(LobbyFailure.ModVersion, "Alex", "0.9").Line.Text(0));
    }

    [Fact]
    public void No_codes_and_no_f_keys_on_any_failure()
    {
        var fKey = new Regex(@"\bF\d+\b");
        foreach (LobbyFailure failure in Enum.GetValues<LobbyFailure>())
        {
            for (int language = 0; language < LobbyWords.LanguageCount; language++)
            {
                string text = JoinFailures.Message(failure, "Alex").Line.Text(language);
                Assert.DoesNotMatch(fKey, text);
                Assert.DoesNotContain(failure.ToString(), text);
            }
        }

        // And not on anything else in the table: the screen that couldn't open names Settings > Online, as every other word does.
        foreach (LobbyWord word in LobbyWords.All)
        {
            Assert.All(word.Texts, text => Assert.DoesNotMatch(fKey, text));
        }
    }

    [Fact]
    public void Null_names_cant_throw()
    {
        foreach (LobbyFailure failure in Enum.GetValues<LobbyFailure>())
        {
            Assert.NotNull(JoinFailures.Message(failure, null!, null!, null!).Line.Text(0));
        }

        Assert.NotNull(JoinFailures.HostToast(RejectReason.SessionFull, null!).Text(0));
    }

    [Theory]
    [InlineData(RejectReason.ProtocolMismatch, "Sam couldn't join: different T2T Online versions.")]
    [InlineData(RejectReason.GameBuildMismatch, "Sam couldn't join: different game updates.")]
    [InlineData(RejectReason.SessionFull, "Sam couldn't join: your lobby is full.")]
    [InlineData(RejectReason.NotInLobby, "Sam couldn't join: they aren't in your Steam lobby.")]
    [InlineData(RejectReason.NoControllerSlot, "Sam couldn't join: no controller could be made for them.")]
    [InlineData(RejectReason.UnsupportedInput, "Sam couldn't join: their kind of controller isn't known here.")]
    [InlineData(RejectReason.HostUnavailable, "Sam couldn't join: online play is off here.")]
    [InlineData(RejectReason.NotAFriend, "Sam couldn't join: they aren't on your Steam friends list.")]
    [InlineData(RejectReason.Removed, "Sam tried to come back: you removed them.")]
    [InlineData(RejectReason.FriendshipUnconfirmed, "Sam couldn't join: Steam couldn't check your friends list. Restart Steam and the game.")]
    [InlineData(RejectReason.None, "Sam couldn't join.")]
    [InlineData((RejectReason)200, "Sam couldn't join.")]
    public void The_hosts_toast_says_who_and_why(RejectReason reason, string english)
    {
        Assert.Equal(english, JoinFailures.HostToast(reason, "Sam").Text(0));
    }

    [Fact]
    public void A_greyed_row_explains_with_the_words_a_real_try_would_get()
    {
        Assert.Equal(JoinFailures.Message(LobbyFailure.Full, "Al"), JoinFailures.ForRow(BrowserBlock.Full, "Al"));
        Assert.Equal(LobbyFailure.GameBuild, JoinFailures.ForRow(BrowserBlock.DifferentBuild, "Al")!.Reason);
        Assert.Contains("0.9", JoinFailures.ForRow(BrowserBlock.DifferentMod, "Al", "0.9", "1.0")!.Line.Text(0));
        Assert.Null(JoinFailures.ForRow(BrowserBlock.None, "Al"));
        Assert.Null(JoinFailures.ForRow(BrowserBlock.Checking, "Al"));
    }
}
