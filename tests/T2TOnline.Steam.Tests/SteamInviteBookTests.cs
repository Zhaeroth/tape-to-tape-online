using System;

namespace T2TOnline.Steam.Tests;

public class SteamInviteBookTests
{
    private const ulong Alex = 76561199000000001UL;
    private const ulong Sam = 76561199000000002UL;
    private const ulong LobbyA = 109775240917663232UL;
    private const ulong LobbyB = 109775240917663233UL;

    [Fact]
    public void An_invite_to_a_lobby_marks_it_until_it_expires()
    {
        var book = new SteamInviteBook();

        Assert.True(book.Add(LobbyA, Alex, TimeSpan.FromSeconds(10)));

        Assert.True(book.IsInvited(LobbyA, TimeSpan.FromSeconds(11)));
        Assert.True(book.TryGet(LobbyA, TimeSpan.FromSeconds(11), out SteamInvite invite));
        Assert.Equal(new SteamInvite(LobbyA, Alex, TimeSpan.FromSeconds(10)), invite);
        Assert.True(book.IsInvited(LobbyA, TimeSpan.FromSeconds(10) + SteamInviteBook.Life));
        Assert.False(book.IsInvited(LobbyA, TimeSpan.FromSeconds(11) + SteamInviteBook.Life));
        Assert.False(book.IsInvited(LobbyB, TimeSpan.FromSeconds(11)));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(76561199000000001UL)] // a user, not a lobby
    [InlineData(1UL)]
    public void Only_a_lobbys_id_is_kept(ulong id)
    {
        var book = new SteamInviteBook();

        Assert.False(book.Add(id, Alex, TimeSpan.Zero));
        Assert.Equal(0, book.Count);
        Assert.Equal(0, book.Version);
    }

    [Fact]
    public void A_newer_invite_to_the_same_lobby_replaces_the_older_and_the_version_changes_with_every_change()
    {
        var book = new SteamInviteBook();
        book.Add(LobbyA, Alex, TimeSpan.Zero);
        int version = book.Version;

        book.Add(LobbyA, Sam, TimeSpan.FromSeconds(5));

        Assert.Equal(1, book.Count);
        Assert.True(book.Version > version);
        Assert.True(book.TryGet(LobbyA, TimeSpan.FromSeconds(6), out SteamInvite invite));
        Assert.Equal(Sam, invite.FromSteamId);

        version = book.Version;
        Assert.True(book.Forget(LobbyA));
        Assert.True(book.Version > version);
        Assert.False(book.Forget(LobbyA));
    }

    [Fact]
    public void The_oldest_invite_goes_when_the_book_is_full()
    {
        var book = new SteamInviteBook();
        for (ulong i = 0; i < SteamInviteBook.Capacity + 1; i++)
        {
            book.Add(LobbyA + i, Alex, TimeSpan.FromSeconds(i));
        }

        Assert.Equal(SteamInviteBook.Capacity, book.Count);
        Assert.False(book.IsInvited(LobbyA, TimeSpan.FromSeconds(20)));
        Assert.True(book.IsInvited(LobbyA + 1, TimeSpan.FromSeconds(20)));
        Assert.True(book.IsInvited(LobbyA + SteamInviteBook.Capacity, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Each_invite_is_announced_once_oldest_first_and_prune_drops_the_expired()
    {
        var book = new SteamInviteBook();
        book.Add(LobbyA, Alex, TimeSpan.FromSeconds(1));
        book.Add(LobbyB, Sam, TimeSpan.FromSeconds(2));

        Assert.True(book.TryTakeUnannounced(TimeSpan.FromSeconds(3), out SteamInvite first));
        Assert.Equal(LobbyA, first.LobbyId);
        Assert.True(book.TryTakeUnannounced(TimeSpan.FromSeconds(3), out SteamInvite second));
        Assert.Equal(LobbyB, second.LobbyId);
        Assert.False(book.TryTakeUnannounced(TimeSpan.FromSeconds(3), out _));
        Assert.True(book.IsInvited(LobbyA, TimeSpan.FromSeconds(3))); // announced is not forgotten

        int version = book.Version;
        book.Prune(TimeSpan.FromSeconds(1) + SteamInviteBook.Life);
        Assert.Equal(version, book.Version); // nothing was older than its life yet
        Assert.Equal(2, book.Count);
        book.Prune(TimeSpan.FromSeconds(1.5) + SteamInviteBook.Life);
        Assert.Equal(1, book.Count); // the first expired, the second hasn't
        Assert.True(book.Version > version);
        book.Prune(TimeSpan.FromSeconds(3) + SteamInviteBook.Life);
        Assert.Equal(0, book.Count);

        book.Add(LobbyA, Alex, TimeSpan.Zero);
        book.Clear();
        Assert.Equal(0, book.Count);
        book.Clear();
    }

    [Fact]
    public void An_invite_that_expired_before_it_was_announced_is_not_announced()
    {
        var book = new SteamInviteBook();
        book.Add(LobbyA, Alex, TimeSpan.Zero);

        Assert.False(book.TryTakeUnannounced(SteamInviteBook.Life + TimeSpan.FromSeconds(1), out _));
    }
}
