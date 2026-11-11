using System;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class MenuAttachmentStoreTests
{
    [Fact]
    public void One_menu_keeps_one_attachment_of_each_kind()
    {
        var store = new MenuAttachmentStore();
        MenuAttachment choices = MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample());
        MenuAttachment line = MenuAttachment.NpcLine(13, NpcLineDataTests.Sample(1));

        Assert.False(store.Put(choices));
        Assert.False(store.Put(line));

        // The line didn't take the choices' place: both are there, each under its own kind.
        Assert.Equal(2, store.Count);
        Assert.Same(choices, store.Get(13, MenuAttachmentKind.EventChoices));
        Assert.Same(line, store.Get(13, MenuAttachmentKind.NpcLine));
        Assert.True(store.Has(13, MenuAttachmentKind.EventChoices) && store.Has(13, MenuAttachmentKind.NpcLine));
        Assert.False(store.Has(13, MenuAttachmentKind.Prompt));
        Assert.Null(store.Get(14, MenuAttachmentKind.EventChoices));
        Assert.Equal(new[] { MenuAttachmentKind.EventChoices, MenuAttachmentKind.NpcLine }, store.Of(13).Select(a => a.Kind));
    }

    [Fact]
    public void A_second_attachment_of_a_kind_replaces_the_first()
    {
        var store = new MenuAttachmentStore();
        store.Put(MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample()));
        store.Put(MenuAttachment.NpcLine(13, NpcLineDataTests.Sample(1)));
        MenuAttachment second = MenuAttachment.NpcLine(13, NpcLineDataTests.Sample(2));
        MenuAttachment page = MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample(version: 1, step: EventSetStep.Page));

        Assert.True(store.Put(second));
        Assert.True(store.Put(page));
        Assert.Equal(2, store.Count);
        Assert.True(store.Get(13, MenuAttachmentKind.NpcLine)!.TryGetNpcLine(out NpcLineData? line));
        Assert.Equal(2, line!.Number);
        Assert.True(store.Get(13, MenuAttachmentKind.EventChoices)!.TryGetEventChoices(out EventChoiceSet? set));
        Assert.Equal(1, set!.Version);
    }

    [Fact]
    public void A_closed_menus_attachments_go_with_it_and_no_others()
    {
        var store = new MenuAttachmentStore();
        store.Put(MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample()));
        store.Put(MenuAttachment.NpcLine(13, NpcLineDataTests.Sample()));
        store.Put(MenuAttachment.RunSummary(20, RunHistoryDataTests.Sample()));
        store.Put(MenuAttachment.Prompt(21, PromptDataTests.Notice()));

        Assert.Equal(2, store.RemoveWhere(serial => serial == 13));
        Assert.Equal(2, store.Count);
        Assert.Empty(store.Of(13));
        Assert.True(store.Has(20, MenuAttachmentKind.RunSummary));
        Assert.Equal(1, store.Remove(21));
        Assert.Equal(0, store.Remove(21));
        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Throws<ArgumentNullException>(() => store.Put(null!));
        Assert.Throws<ArgumentNullException>(() => store.RemoveWhere(null!));
    }
}
