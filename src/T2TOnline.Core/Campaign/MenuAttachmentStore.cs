using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's attachments a friend holds: the newest one of each kind for each open menu, by (serial, kind). A menu can carry
/// several kinds at once (an event menu: its choices and its NPC's newest line), and a second attachment of a kind replaces
/// the first (a new set of choices, the next line). The caller drops a menu's attachments when the menu closes. Only
/// attachments that read (<see cref="MenuAttachmentMessage"/>) go in, so the kinds are the known ones and there's at most
/// one per kind for each menu the friend's mirror holds open.
/// </summary>
public sealed class MenuAttachmentStore
{
    private readonly Dictionary<(uint Serial, MenuAttachmentKind Kind), MenuAttachment> _held = new();

    /// <summary>How many attachments are held.</summary>
    public int Count => _held.Count;

    /// <summary>Keeps <paramref name="attachment"/> for its menu and kind, in place of one held before. True when it replaced one.</summary>
    public bool Put(MenuAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        bool replaced = _held.ContainsKey((attachment.Serial, attachment.Kind));
        _held[(attachment.Serial, attachment.Kind)] = attachment;
        return replaced;
    }

    /// <summary>Menu <paramref name="serial"/>'s attachment of <paramref name="kind"/>, or null.</summary>
    public MenuAttachment? Get(uint serial, MenuAttachmentKind kind) => _held.TryGetValue((serial, kind), out MenuAttachment? attachment) ? attachment : null;

    /// <summary>True when menu <paramref name="serial"/> has an attachment of <paramref name="kind"/>.</summary>
    public bool Has(uint serial, MenuAttachmentKind kind) => _held.ContainsKey((serial, kind));

    /// <summary>Menu <paramref name="serial"/>'s attachments, in the order of their kinds.</summary>
    public IReadOnlyList<MenuAttachment> Of(uint serial) =>
        _held.Where(p => p.Key.Serial == serial).OrderBy(p => p.Key.Kind).Select(p => p.Value).ToList();

    /// <summary>Drops menu <paramref name="serial"/>'s attachments. Returns how many there were.</summary>
    public int Remove(uint serial) => RemoveWhere(s => s == serial);

    /// <summary>Drops the attachments of every menu <paramref name="gone"/> says is gone. Returns how many went.</summary>
    public int RemoveWhere(Func<uint, bool> gone)
    {
        ArgumentNullException.ThrowIfNull(gone);
        List<(uint, MenuAttachmentKind)> drop = _held.Keys.Where(k => gone(k.Serial)).ToList();
        foreach ((uint, MenuAttachmentKind) key in drop)
        {
            _held.Remove(key);
        }

        return drop.Count;
    }

    /// <summary>Drops everything.</summary>
    public void Clear() => _held.Clear();
}
