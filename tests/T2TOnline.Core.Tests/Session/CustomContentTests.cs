using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Session;

/// <summary>Custom content (teams, skaters, goalies, logos that exist only on the host's PC): items, references, the message, sending and putting back together.</summary>
public class CustomContentTests
{
    [Fact]
    public void A_logos_log_text_leaves_its_file_name_out()
    {
        // A logo's key is the file name its player chose: a log line names the logo by the hash of its bytes only (D127).
        Assert.Equal("logo (00000000000000ab)", new CustomRef(CustomContentKind.Logo, 0xAB, "custom_Riley Casey.png").ToString());
        Assert.Equal("runlogo (00000000000000ab)", new CustomRef(CustomContentKind.RunLogo, 0xAB, "custom_Riley Casey.png").ToString());
        Assert.Equal("team T_1 (00000000000000ab)", new CustomRef(CustomContentKind.Team, 0xAB, "T_1").ToString());
        Assert.Equal("logo:00000000000000ab:custom_Riley Casey.png", new CustomRef(CustomContentKind.Logo, 0xAB, "custom_Riley Casey.png").Format());
    }

    [Fact]
    public void A_hosts_key_that_isnt_shaped_like_an_id_is_written_by_its_length()
    {
        // A reference's key passes the file-name check with spaces and any letters, so a modified host can put a name there.
        Assert.True(CustomRef.TryParse("skater:00000000000000ab:Riley Casey", out CustomRef named));
        Assert.Equal("skater a key of 11 characters (00000000000000ab)", named.ToString());
        Assert.Equal("skater:00000000000000ab:Riley Casey", named.Format()); // the setup still carries it as sent

        // The game's own ids for custom content (GUIDs) read as before.
        Assert.Equal("team 0f8fad5b-d9cb-469f-a165-70867728950e (00000000000000ab)",
            new CustomRef(CustomContentKind.Team, 0xAB, "0f8fad5b-d9cb-469f-a165-70867728950e").ToString());
    }

    private const ulong Host = 100;
    private const ulong Friend = 200;

    [Theory]
    [InlineData(CustomContentKind.Team, "0f8fad5b-d9cb-469f-a165-70867728950e", 10, true)]
    [InlineData(CustomContentKind.Skater, "skater-1", CustomItem.MaxModelBytes, true)]
    [InlineData(CustomContentKind.Goalie, "g", 1, true)]
    [InlineData(CustomContentKind.Logo, "custom_My Logo (2).png", CustomItem.MaxLogoBytes, true)]
    [InlineData(CustomContentKind.RunLogo, "custom_a.PNG", 5, true)]
    [InlineData(CustomContentKind.None, "x", 1, false)]
    [InlineData(CustomContentKind.RunLook, "run-look", 100, true)]
    [InlineData((CustomContentKind)7, "x", 1, false)]
    [InlineData(CustomContentKind.Team, "", 1, false)]
    [InlineData(CustomContentKind.Team, "x", 0, false)]
    [InlineData(CustomContentKind.Skater, "x", CustomItem.MaxModelBytes + 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_a.png", CustomItem.MaxLogoBytes + 1, false)]
    [InlineData(CustomContentKind.Logo, "a.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_a.jpg", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_../a.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_..\\a.png", 1, false)]
    [InlineData(CustomContentKind.Team, "c:x", 1, false)]
    [InlineData(CustomContentKind.Team, "a\u0001b", 1, false)]
    [InlineData(CustomContentKind.Team, " a", 1, false)]
    [InlineData(CustomContentKind.Team, "a.", 1, false)]
    [InlineData(CustomContentKind.Team, "a..b", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_CON.png", 1, false)] // file names Windows opens as devices
    [InlineData(CustomContentKind.Logo, "custom_nul.png", 1, false)]
    [InlineData(CustomContentKind.RunLogo, "custom_Aux.PNG", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_prn.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_COM1.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_com0.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_LPT9.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_COM¹.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_lpt³.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_CON .png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_nul.x.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_CONIN$.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_conout$.png", 1, false)]
    [InlineData(CustomContentKind.Logo, "custom_CONSOLE.png", 1, true)]
    [InlineData(CustomContentKind.Logo, "custom_COM10.png", 1, true)]
    [InlineData(CustomContentKind.Logo, "custom_COM.png", 1, true)]
    [InlineData(CustomContentKind.Logo, "custom_NULL.png", 1, true)]
    [InlineData(CustomContentKind.Logo, "custom_a CON.png", 1, true)]
    [InlineData(CustomContentKind.Team, "CON", 1, true)] // only a logo's key is a file name
    public void Items_hold_only_safe_keys_and_sizes(CustomContentKind kind, string key, int length, bool ok)
    {
        Assert.Equal(ok, CustomItem.Check(kind, key, length) == null);
        if (ok && length <= 64)
        {
            Assert.Equal(key, new CustomItem(kind, key, new byte[length]).Key);
        }
        else if (!ok && length is > 0 and <= 64)
        {
            Assert.Throws<ArgumentException>(() => new CustomItem(kind, key, new byte[length]));
        }
    }

    [Fact]
    public void A_key_over_its_limit_in_bytes_is_refused()
    {
        Assert.Null(CustomItem.Check(CustomContentKind.Team, new string('a', CustomItem.MaxKeyBytes), 1));
        Assert.NotNull(CustomItem.Check(CustomContentKind.Team, new string('a', CustomItem.MaxKeyBytes + 1), 1));
        Assert.NotNull(CustomItem.Check(CustomContentKind.Team, new string('é', (CustomItem.MaxKeyBytes / 2) + 1), 1));
    }

    [Fact]
    public void The_hash_covers_kind_key_and_bytes()
    {
        ulong hash = new CustomItem(CustomContentKind.Team, "a", new byte[] { 1, 2 }).Hash;
        Assert.Equal(hash, new CustomItem(CustomContentKind.Team, "a", new byte[] { 1, 2 }).Hash);
        Assert.NotEqual(hash, new CustomItem(CustomContentKind.Skater, "a", new byte[] { 1, 2 }).Hash);
        Assert.NotEqual(hash, new CustomItem(CustomContentKind.Team, "b", new byte[] { 1, 2 }).Hash);
        Assert.NotEqual(hash, new CustomItem(CustomContentKind.Team, "a", new byte[] { 1, 3 }).Hash);
        Assert.NotEqual(hash, new CustomItem(CustomContentKind.Team, "a", new byte[] { 1, 2, 0 }).Hash);
        Assert.Equal("my logo.png", CustomItem.LogoFileName("custom_my logo.png"));
    }

    [Fact]
    public void A_reference_reads_back_from_its_text_and_junk_does_not()
    {
        var item = new CustomItem(CustomContentKind.Logo, "custom_Crest v2.png", new byte[] { 9 });
        string text = item.Ref.Format();
        Assert.StartsWith("logo:", text, StringComparison.Ordinal);
        Assert.True(CustomRef.TryParse(text, out CustomRef back));
        Assert.Equal(item.Ref, back);
        Assert.True(CustomRef.TryParse("team:00000000000000ff:id-1", out CustomRef team));
        Assert.Equal(new CustomRef(CustomContentKind.Team, 255, "id-1"), team);

        foreach (string junk in new[]
        {
            string.Empty, "team", "team:", "team:00ff:id", "teams:00000000000000ff:id", "team:0000000000000g0f:id", "team:00000000000000ff:",
            "logo:00000000000000ff:a.png", "team:00000000000000ff:a/b", ":00000000000000ff:a", "team:00000000000000ff",
        })
        {
            Assert.False(CustomRef.TryParse(junk, out _), junk);
        }

        Assert.False(CustomRef.TryParse(null, out _));
    }

    [Fact]
    public void A_setup_names_its_custom_content_and_reads_it_back()
    {
        var refs = new List<CustomRef>
        {
            new(CustomContentKind.Team, 1, "t"), new(CustomContentKind.Skater, 2, "s"), new(CustomContentKind.Logo, 3, "custom_x.png"),
        };
        var setup = new List<KeyValuePair<string, NetValue>> { new("mode.gameType", NetValue.FromInt(0)) };
        CustomContentValues.Add((k, v) => setup.Add(new(k, v)), refs);
        Assert.Equal(1 + 1 + refs.Count, setup.Count);
        Assert.True(CustomContentValues.TryRead(setup, out List<CustomRef> read, out string problem), problem);
        Assert.Equal(refs, read);

        // None named: nothing added, nothing read.
        var plain = new List<KeyValuePair<string, NetValue>>();
        CustomContentValues.Add((k, v) => plain.Add(new(k, v)), Array.Empty<CustomRef>());
        Assert.Empty(plain);
        Assert.True(CustomContentValues.TryRead(plain, out read, out _));
        Assert.Empty(read);
        Assert.Throws<ArgumentException>(() => CustomContentValues.Add((_, _) => { }, Enumerable.Range(0, CustomContentValues.MaxRefs + 1).Select(i => new CustomRef(CustomContentKind.Team, (ulong)i, "t")).ToList()));
    }

    [Fact]
    public void A_setup_with_bad_references_is_refused()
    {
        static List<KeyValuePair<string, NetValue>> With(params (string Key, NetValue Value)[] values) => values.Select(v => new KeyValuePair<string, NetValue>(v.Key, v.Value)).ToList();
        Assert.False(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromInt(0))), out _, out _));
        Assert.False(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromInt(CustomContentValues.MaxRefs + 1))), out _, out _));
        Assert.False(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromString("1"))), out _, out _));
        Assert.False(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromInt(2)), ("custom.0", NetValue.FromString("team:0000000000000001:a"))), out _, out string problem));
        Assert.Contains("custom.1", problem, StringComparison.Ordinal);
        Assert.False(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromInt(1)), ("custom.0", NetValue.FromInt(5))), out _, out _));

        // The same reference twice counts once.
        Assert.True(CustomContentValues.TryRead(With((CustomContentValues.CountKey, NetValue.FromInt(2)), ("custom.0", NetValue.FromString("team:0000000000000001:a")),
            ("custom.1", NetValue.FromString("team:0000000000000001:a"))), out List<CustomRef> refs, out _));
        Assert.Single(refs);
    }

    [Fact]
    public void A_piece_reads_back_as_written()
    {
        var item = new CustomItem(CustomContentKind.Skater, "s-1", Encoding.UTF8.GetBytes("{\"id\":\"s-1\"}"));
        var writer = new BitWriter();
        Assert.Equal(item.Bytes.Length, CustomContentMessage.Write(writer, item, 0));
        var reader = new BitReader(writer.AsSpan());
        Assert.True(CustomContentMessage.TryRead(ref reader, out CustomChunk chunk));
        Assert.Equal(item.Hash, chunk.Hash);
        Assert.Equal(item.Kind, chunk.Kind);
        Assert.Equal(item.Key, chunk.Key);
        Assert.Equal(item.Bytes.Length, chunk.TotalBytes);
        Assert.Equal(0, chunk.Offset);
        Assert.Equal(item.Bytes, chunk.Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomContentMessage.Write(new BitWriter(), item, item.Bytes.Length));
    }

    [Fact]
    public void A_large_logo_goes_in_pieces_and_comes_back_whole()
    {
        byte[] png = Png(64, 32, extra: (CustomContentMessage.MaxChunkBytes * 2) + 1000);
        var item = new CustomItem(CustomContentKind.Logo, "custom_crest.png", png);
        var transport = new Capture();
        var sender = new CustomContentSender(transport);
        Assert.Equal(1, sender.Send(Friend, new[] { item }));
        Assert.Equal(3, transport.Sent.Count);
        Assert.All(transport.Sent, m => Assert.Equal(Channel.Bulk, m.Channel));
        Assert.All(transport.Sent, m => Assert.True(m.Bytes.Length <= CustomContentMessage.MaxChunkBytes + 256));
        Assert.True(sender.HasSent(Friend, item));

        var client = new MatchStreamClient(transport, Host);
        foreach ((ulong _, Channel channel, byte[] bytes) in transport.Sent)
        {
            Assert.True(client.Receive(Host, channel, bytes, 0));
        }

        Assert.True(client.Content.TryGet(item.Hash, out CustomItem? back));
        Assert.Equal(png, back!.Bytes);
        Assert.Equal("complete", client.Content.Progress(item.Hash));
        Assert.True(client.Content.TryTakeCompleted(out CustomItem? completed));
        Assert.Same(back, completed);
        Assert.False(client.Content.TryTakeCompleted(out _));
        Assert.Equal(0, client.Malformed);

        // Again (a resend after a rejoin): held already, fine.
        Assert.True(client.Receive(Host, Channel.Bulk, transport.Sent[0].Bytes, 0));
        Assert.Equal(1, client.Content.Duplicates);

        // Only the bulk channel carries it (reliable and ordered); and only from the host.
        Assert.False(client.Receive(Host, Channel.Events, transport.Sent[0].Bytes, 0));
        Assert.False(client.Receive(Friend, Channel.Bulk, transport.Sent[0].Bytes, 0));
    }

    [Fact]
    public void Each_item_goes_to_each_friend_once_and_all_again_after_a_rejoin()
    {
        var transport = new Capture();
        var sender = new CustomContentSender(transport);
        CustomItem[] items = { Item(CustomContentKind.Team, "t"), Item(CustomContentKind.Skater, "s") };
        Assert.Equal(2, sender.Send(Friend, items));
        Assert.Equal(0, sender.Send(Friend, items));
        Assert.Equal(2, sender.Send(Friend + 1, items));
        Assert.Equal(1, sender.Send(Friend, items.Append(Item(CustomContentKind.Goalie, "g")).ToList()));
        sender.Forget(Friend);
        Assert.Equal(2, sender.Send(Friend, items));
        Assert.Equal(7, sender.ItemsSent);
        sender.Clear();
        Assert.False(sender.HasSent(Friend + 1, items[0]));
    }

    [Fact]
    public void A_refused_send_is_tried_again_next_time()
    {
        var transport = new Capture { Refuse = true };
        var sender = new CustomContentSender(transport);
        CustomItem item = Item(CustomContentKind.Team, "t");
        Assert.Equal(0, sender.Send(Friend, new[] { item }));
        Assert.False(sender.HasSent(Friend, item));
        Assert.Equal(1, sender.Refused);
        transport.Refuse = false;
        Assert.Equal(1, sender.Send(Friend, new[] { item }));
        Assert.True(sender.HasSent(Friend, item));
    }

    [Fact]
    public void Pieces_out_of_order_or_disagreeing_are_refused_and_a_resend_from_the_start_works()
    {
        CustomItem item = new(CustomContentKind.Logo, "custom_a.png", Png(8, 8, extra: (CustomContentMessage.MaxChunkBytes * 2) + 10));
        CustomChunk[] pieces = Pieces(item);
        Assert.Equal(3, pieces.Length);

        var receiver = new CustomContentReceiver();
        Assert.False(receiver.Receive(pieces[1])); // no start
        Assert.True(receiver.Receive(pieces[0]));
        Assert.False(receiver.Receive(pieces[2])); // skipped one: dropped
        Assert.Equal("not here", receiver.Progress(item.Hash));
        Assert.Equal(0, receiver.StoredBytes);

        Assert.True(receiver.Receive(pieces[0]));
        Assert.Contains("of", receiver.Progress(item.Hash), StringComparison.Ordinal);
        Assert.True(receiver.Receive(pieces[0])); // from the start again
        Assert.True(receiver.Receive(pieces[1]));
        Assert.True(receiver.Receive(pieces[2]));
        Assert.True(receiver.TryGet(item.Hash, out _));

        // A piece claiming another key, kind or length for an item in flight drops it.
        var second = new CustomContentReceiver();
        Assert.True(second.Receive(pieces[0]));
        Assert.False(second.Receive(pieces[1] with { Key = "custom_b.png" }));
        Assert.False(second.TryGet(item.Hash, out _));
        Assert.Equal(0, second.StoredBytes);

        // Bytes that don't match the hash are never kept.
        var third = new CustomContentReceiver();
        Assert.False(third.Receive(new CustomChunk(12345, CustomContentKind.Team, "t", 3, 0, new byte[] { 1, 2, 3 })));
        Assert.Equal(0, third.Count);
        Assert.Equal(0, third.StoredBytes);
        Assert.Equal(1, third.Rejected);
    }

    [Fact]
    public void What_is_kept_and_in_flight_is_capped()
    {
        var receiver = new CustomContentReceiver();
        for (int i = 0; i < CustomContentReceiver.MaxItems + 5; i++)
        {
            CustomItem item = Item(CustomContentKind.Skater, $"s{i}");
            Assert.True(receiver.Receive(Pieces(item)[0]));
        }

        Assert.Equal(CustomContentReceiver.MaxItems, receiver.Count);
        Assert.Equal(5, receiver.Evicted);
        Assert.False(receiver.TryGet(Item(CustomContentKind.Skater, "s0").Hash, out _));
        Assert.True(receiver.TryGet(Item(CustomContentKind.Skater, $"s{CustomContentReceiver.MaxItems + 4}").Hash, out _));

        // Starts that never finish: the oldest is dropped for a new one; the bytes held stay bounded.
        var partials = new CustomContentReceiver();
        for (int i = 0; i < CustomContentReceiver.MaxInFlight + 3; i++)
        {
            var big = new CustomItem(CustomContentKind.Logo, $"custom_{i}.png", Png(8, 8, extra: CustomItem.MaxLogoBytes - 100));
            Assert.True(partials.Receive(Pieces(big)[0]));
        }

        Assert.True(partials.StoredBytes <= CustomContentReceiver.MaxStoredBytes);
        Assert.True(partials.Rejected >= 3);
        Assert.Equal(0, partials.Count);
        partials.Clear();
        Assert.Equal(0, partials.StoredBytes);
    }

    [Fact]
    public void An_item_the_friend_s_store_dropped_to_make_room_goes_again_when_a_later_send_names_it()
    {
        // A long session: the first match's team, then more of the host's content than a friend's store keeps.
        var link = new Link();
        CustomItem first = Item(CustomContentKind.Team, "first");
        link.Sender.Send(Friend, new[] { first });
        for (int i = 0; i < CustomContentReceiver.MaxItems; i++)
        {
            link.Sender.Send(Friend, new[] { Item(CustomContentKind.Skater, $"s{i}") });
        }

        link.Deliver();
        Assert.False(link.Store.TryGet(first.Hash, out _)); // dropped to make room
        Assert.False(link.Sender.HasSent(Friend, first));
        Assert.Equal(1, link.Sender.Dropped);

        // A later match with that team again: it goes again, and the friend's load finds it.
        Assert.Equal(1, link.Sender.Send(Friend, new[] { first }));
        link.Deliver();
        Assert.True(link.Store.TryGet(first.Hash, out _));
        Assert.Equal(0, link.Client.Malformed);
    }

    [Fact]
    public void An_item_a_send_names_that_its_own_new_items_push_out_goes_again_after_them()
    {
        // The friend's store is full and the oldest item in it is one the next match needs, with a new one.
        var link = new Link();
        CustomItem team = Item(CustomContentKind.Team, "team");
        link.Sender.Send(Friend, new[] { team });
        for (int i = 1; i < CustomContentReceiver.MaxItems; i++)
        {
            link.Sender.Send(Friend, new[] { Item(CustomContentKind.Skater, $"s{i}") });
        }

        link.Deliver();
        Assert.Equal(CustomContentReceiver.MaxItems, link.Store.Count);
        CustomItem logo = new(CustomContentKind.Logo, "custom_crest.png", Png(16, 16));
        Assert.Equal(2, link.Sender.Send(Friend, new[] { team, logo }));
        link.Deliver();

        Assert.True(link.Store.TryGet(team.Hash, out _));
        Assert.True(link.Store.TryGet(logo.Hash, out _));
        Assert.Equal(CustomContentReceiver.MaxItems, link.Store.Count);
    }

    [Fact]
    public void Large_items_past_the_store_s_bytes_go_again_too_and_one_still_held_is_only_a_duplicate()
    {
        var link = new Link();
        const int Size = 3 * 1024 * 1024;
        CustomItem[] logos = Enumerable.Range(0, 11).Select(i => new CustomItem(CustomContentKind.Logo, $"custom_{i}.png", Png(16, 16, extra: Size))).ToArray();
        foreach (CustomItem logo in logos)
        {
            link.Sender.Send(Friend, new[] { logo });
        }

        link.Deliver();
        Assert.False(link.Store.TryGet(logos[0].Hash, out _)); // 32 MB hold ten: the eleventh pushed the first out
        Assert.True(link.Store.TryGet(logos[1].Hash, out _));

        // The host counts one item short of the store's bytes, so it had counted the second as dropped too: both go again.
        Assert.Equal(2, link.Sender.Send(Friend, new[] { logos[0], logos[1] }));
        link.Deliver();
        Assert.True(link.Store.TryGet(logos[0].Hash, out _));
        Assert.True(link.Store.TryGet(logos[1].Hash, out _));

        // The third is still in the store though the host counted it as dropped: sent again, it's a duplicate there.
        Assert.True(link.Store.TryGet(logos[3].Hash, out _));
        Assert.False(link.Sender.HasSent(Friend, logos[3]));
        Assert.Equal(1, link.Sender.Send(Friend, new[] { logos[3] }));
        link.Deliver();
        Assert.True(link.Store.TryGet(logos[3].Hash, out _));
        Assert.True(link.Store.Duplicates > 0);
        Assert.True(link.Store.StoredBytes <= CustomContentReceiver.MaxStoredBytes);
        Assert.All(logos.Where(l => link.Sender.HasSent(Friend, l)), l => Assert.True(link.Store.TryGet(l.Hash, out _)));
    }

    [Fact]
    public void An_item_sent_again_in_full_counts_as_the_newest_in_the_store()
    {
        var receiver = new CustomContentReceiver();
        CustomItem[] items = Enumerable.Range(0, CustomContentReceiver.MaxItems).Select(i => Item(CustomContentKind.Skater, $"s{i}")).ToArray();
        foreach (CustomItem item in items)
        {
            Assert.True(receiver.Receive(Pieces(item)[0]));
        }

        // The oldest again (a rejoin's resend): the next new item pushes out the second oldest instead.
        Assert.True(receiver.Receive(Pieces(items[0])[0]));
        Assert.True(receiver.Receive(Pieces(Item(CustomContentKind.Goalie, "g"))[0]));
        Assert.True(receiver.TryGet(items[0].Hash, out _));
        Assert.False(receiver.TryGet(items[1].Hash, out _));
    }

    [Fact]
    public void Items_left_part_way_never_make_the_store_drop_what_the_host_counts_as_held()
    {
        // Two large logos start and never finish (a piece of each is refused, and the host has moved on to other teams):
        // the friend's store holds their room, which the host's count of that store can't see.
        var link = new Link();
        const int Size = 3 * 1024 * 1024;
        foreach (string left in new[] { "left1", "left2" })
        {
            link.RefuseAfter(1);
            Assert.Equal(0, link.Sender.Send(Friend, new[] { new CustomItem(CustomContentKind.Logo, $"custom_{left}.png", Png(16, 16, extra: Size)) }));
            link.Deliver();
        }

        Assert.Equal(2, link.Sender.Refused);
        Assert.True(link.Store.StoredBytes > 2 * Size);
        Assert.Equal(0, link.Store.Count);

        // Nine more go in full: within what the host counts as held, so it counts none of them as dropped.
        CustomItem[] logos = Enumerable.Range(0, 9).Select(i => new CustomItem(CustomContentKind.Logo, $"custom_{i}.png", Png(16, 16, extra: Size))).ToArray();
        foreach (CustomItem logo in logos)
        {
            Assert.Equal(1, link.Sender.Send(Friend, new[] { logo }));
        }

        link.Deliver();
        Assert.Equal(0, link.Sender.Dropped);
        Assert.All(logos, logo => Assert.True(link.Sender.HasSent(Friend, logo)));

        // The store made its room from the items left part way, never from one the host won't send again.
        Assert.All(logos, logo => Assert.True(link.Store.TryGet(logo.Hash, out _)));
        Assert.True(link.Store.StoredBytes <= CustomContentReceiver.MaxStoredBytes);
        Assert.Equal(0, link.Client.Malformed);
    }

    [Fact]
    public void A_cut_or_damaged_piece_never_reads_and_never_throws()
    {
        var item = new CustomItem(CustomContentKind.Team, "team-1", Encoding.UTF8.GetBytes("{\"Id\":\"team-1\",\"TeamName\":\"Crows\"}"));
        var writer = new BitWriter();
        CustomContentMessage.Write(writer, item, 0);
        byte[] bytes = writer.ToArray();
        for (int length = 0; length < bytes.Length; length++)
        {
            var reader = new BitReader(bytes.AsSpan(0, length));
            Assert.False(CustomContentMessage.TryRead(ref reader, out _));
        }

        var random = new Random(67);
        var receiver = new CustomContentReceiver();
        for (int i = 0; i < 5000; i++)
        {
            byte[] junk = (byte[])bytes.Clone();
            if (i % 3 == 0)
            {
                junk = new byte[random.Next(0, 200)];
                random.NextBytes(junk);
            }
            else
            {
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    junk[random.Next(junk.Length)] ^= (byte)random.Next(1, 256);
                }
            }

            var reader = new BitReader(junk);
            if (CustomContentMessage.TryRead(ref reader, out CustomChunk chunk))
            {
                Assert.True(chunk.Offset + chunk.Bytes.Length <= chunk.TotalBytes);
                Assert.Null(CustomItem.Check(chunk.Kind, chunk.Key, chunk.TotalBytes));
                receiver.Receive(chunk);
            }
        }

        // Whatever got through was checked against its hash: at most the real item is held.
        Assert.True(receiver.Count <= 1);
        Assert.True(receiver.Count == 0 || receiver.TryGet(item.Hash, out _));
    }

    [Fact]
    public void A_piece_claiming_sizes_past_the_limits_is_refused_before_anything_is_allocated()
    {
        static byte[] Forged(CustomContentKind kind, string key, ulong total, ulong offset, ulong length, int body)
        {
            var w = new BitWriter();
            w.WriteUInt64(1);
            w.WriteByte((byte)kind);
            w.WriteShortString(key, CustomItem.MaxKeyBytes);
            w.WriteVarUInt(total);
            w.WriteVarUInt(offset);
            w.WriteVarUInt(length);
            w.WriteBytes(new byte[body]);
            return w.ToArray();
        }

        static bool Reads(byte[] bytes)
        {
            var reader = new BitReader(bytes);
            return CustomContentMessage.TryRead(ref reader, out _);
        }

        Assert.True(Reads(Forged(CustomContentKind.Team, "t", 4, 0, 4, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", CustomItem.MaxModelBytes + 1UL, 0, 4, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Logo, "custom_a.png", CustomItem.MaxLogoBytes + 1UL, 0, 4, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 4, 4, 1, 1)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 4, 2, 4, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 4, 0, 0, 0)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 0, 0, 1, 1)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 1UL << 40, 0, 1UL << 39, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Logo, "t", 4, 0, 4, 4)));
        Assert.False(Reads(Forged((CustomContentKind)9, "t", 4, 0, 4, 4)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 400_000, 0, CustomContentMessage.MaxChunkBytes + 1UL, 8)));
        Assert.False(Reads(Forged(CustomContentKind.Team, "t", 4, 0, 4, 3)));
    }

    [Fact]
    public void A_logo_is_decoded_only_when_its_png_header_says_it_is_small_enough()
    {
        Assert.True(PngHeader.TryRead(Png(300, 200), out int width, out int height));
        Assert.Equal((300, 200), (width, height));
        Assert.Null(PngHeader.CheckLogo(Png(PngHeader.MaxSide, 1)));
        Assert.Contains("pixels", PngHeader.CheckLogo(Png(PngHeader.MaxSide + 1, 1)), StringComparison.Ordinal);
        Assert.Contains("pixels", PngHeader.CheckLogo(Png(1, 60_000)), StringComparison.Ordinal);
        Assert.Equal("it isn't a PNG", PngHeader.CheckLogo(Encoding.ASCII.GetBytes("GIF89a....................")));
        byte[] notIhdr = Png(4, 4);
        notIhdr[12] = (byte)'X';
        Assert.False(PngHeader.TryRead(notIhdr, out _, out _));
        Assert.False(PngHeader.TryRead(Png(0, 4), out _, out _));
        Assert.False(PngHeader.TryRead(Png(4, 4).AsSpan(0, 20), out _, out _));
    }

    [Fact]
    public void A_device_name_piece_never_reads_whatever_its_case_or_extension()
    {
        // The key becomes a file name on the friend's PC; the decoder refuses a device's name first.
        static bool Reads(string key)
        {
            var w = new BitWriter();
            w.WriteUInt64(1);
            w.WriteByte((byte)CustomContentKind.Logo);
            w.WriteShortString(key, CustomItem.MaxKeyBytes);
            w.WriteVarUInt(4);
            w.WriteVarUInt(0);
            w.WriteVarUInt(4);
            w.WriteBytes(new byte[4]);
            var reader = new BitReader(w.ToArray());
            return CustomContentMessage.TryRead(ref reader, out _);
        }

        Assert.True(Reads("custom_NUX.png"));
        Assert.False(Reads("custom_NUL.png"));
        Assert.False(Reads("custom_com3.Png"));
        Assert.False(CustomRef.TryParse("logo:0123456789abcdef:custom_NUL.png", out _));
        Assert.True(CustomRef.TryParse("logo:0123456789abcdef:custom_NUX.png", out _));

        Assert.True(CustomItem.IsDeviceName("nul"));
        Assert.True(CustomItem.IsDeviceName("Com7.tar.png"));
        Assert.True(CustomItem.IsDeviceName("AUX   .png"));
        Assert.False(CustomItem.IsDeviceName("auxiliary.png"));
        Assert.False(CustomItem.IsDeviceName(".png"));
        Assert.False(CustomItem.IsDeviceName(string.Empty));
    }

    [Fact]
    public void A_session_decodes_only_so_many_logos_and_pixels()
    {
        // A host sending logo after logo can't make a friend decode textures without end.
        var budget = new CustomLogoBudget();
        Assert.True(budget.TryTake(PngHeader.MaxSide, PngHeader.MaxSide, out _));
        Assert.True(budget.TryTake(PngHeader.MaxSide, PngHeader.MaxSide, out _));
        Assert.True(budget.TryTake(PngHeader.MaxSide, PngHeader.MaxSide, out _));
        Assert.True(budget.TryTake(PngHeader.MaxSide, PngHeader.MaxSide, out _));
        Assert.False(budget.TryTake(1, 1, out string problem)); // the pixels are spent
        Assert.Contains("the most it takes", problem, StringComparison.Ordinal);
        Assert.Equal((4, CustomLogoBudget.MaxPixels, 1), (budget.Logos, budget.Pixels, budget.Refused));

        budget.Reset();
        for (int i = 0; i < CustomLogoBudget.MaxLogos; i++)
        {
            Assert.True(budget.TryTake(256, 256, out _));
        }

        Assert.False(budget.TryTake(16, 16, out _)); // the count is spent
        Assert.Equal(CustomLogoBudget.MaxLogos, budget.Logos);
        Assert.False(budget.TryTake(-5, int.MaxValue, out _));
        Assert.Equal(2, budget.Refused);
    }

    /// <summary>The start of a PNG of <paramref name="width"/> by <paramref name="height"/>: signature, IHDR, then filler.</summary>
    internal static byte[] Png(int width, int height, int extra = 16)
    {
        byte[] bytes = new byte[33 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static CustomItem Item(CustomContentKind kind, string key) => new(kind, key, Encoding.UTF8.GetBytes($"{{\"id\":\"{key}\"}}"));

    private static CustomChunk[] Pieces(CustomItem item)
    {
        var pieces = new List<CustomChunk>();
        int offset = 0;
        while (offset < item.Bytes.Length)
        {
            var writer = new BitWriter();
            offset = CustomContentMessage.Write(writer, item, offset);
            var reader = new BitReader(writer.AsSpan());
            Assert.True(CustomContentMessage.TryRead(ref reader, out CustomChunk chunk));
            pieces.Add(chunk);
        }

        return pieces.ToArray();
    }

    /// <summary>A host's sender and a friend's stream client, with what was sent handed over in order on demand.</summary>
    private sealed class Link
    {
        private readonly Capture _transport = new();
        private int _delivered;

        public Link()
        {
            Sender = new CustomContentSender(_transport);
            Client = new MatchStreamClient(_transport, Host);
        }

        public CustomContentSender Sender { get; }

        public MatchStreamClient Client { get; }

        public CustomContentReceiver Store => Client.Content;

        /// <summary>The next <paramref name="sends"/> pieces go, then one is refused (the friend's link overflowed).</summary>
        public void RefuseAfter(int sends) => _transport.RefuseAfter = sends;

        public void Deliver()
        {
            for (; _delivered < _transport.Sent.Count; _delivered++)
            {
                (ulong _, Channel channel, byte[] bytes) = _transport.Sent[_delivered];
                Assert.True(Client.Receive(Host, channel, bytes, 0));
            }
        }
    }

    private sealed class Capture : ISessionTransport
    {
        public List<(ulong Peer, Channel Channel, byte[] Bytes)> Sent { get; } = new();

        public bool Refuse { get; set; }

        /// <summary>How many more sends go before one is refused, once (-1: none is).</summary>
        public int RefuseAfter { get; set; } = -1;

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (Refuse)
            {
                return false;
            }

            if (RefuseAfter == 0)
            {
                RefuseAfter = -1;
                return false;
            }

            if (RefuseAfter > 0)
            {
                RefuseAfter--;
            }

            Sent.Add((peer, channel, message.ToArray()));
            return true;
        }
    }
}
