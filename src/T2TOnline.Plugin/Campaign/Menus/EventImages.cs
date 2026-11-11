#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppData;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue;
using Il2CppRogue.NPC.ConditioningNPC;
using Il2CppRogue.NPC.FanNumber1NPC;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// An event choice's images, and a Fan Number 1 preview's icon, as the exact sprites the host's game used. A sprite's name
/// alone may match several loaded sprites (two stat icons of one name in different atlases, say), so each image carries where it came
/// from (<see cref="ImageSource"/>):
/// <list type="bullet">
/// <item>Host (<see cref="HostSprites"/>): by object, the icon of one of the choice's objects (the General Manager's images
/// are its agent's talent icons, verified in the game's code), one of the event's own sprites (a training's or Fan Number
/// 1's stat icons, Fan Number 1's size icons), or none; and always the sprite's texture name and rect.</item>
/// <item>Friend (<see cref="Resolver"/>): the exact source first (an object's icon, or the event's own sprite of that name),
/// checked by name; else the loaded sprites agreeing on name, texture and rect (the same image); none leaves the image out
/// (WARN), never "the first".</item>
/// </list>
/// </summary>
internal static class EventImages
{
    private const int NameChars = 48;

    // Most names said of each kind below (a host's words, as many as it likes): past this the build's line only counts them.
    private const int MaxSaid = 64;

    // Images already said to be missing here, or found only by name when their source wasn't there, by name (logged once each).
    private static readonly HashSet<string> SaidMissing = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SaidByName = new(StringComparer.Ordinal);

    /// <summary>The event's own sprites (by pointer and by name): a training's stat icons, Fan Number 1's stat and size icons.</summary>
    private static IEnumerable<Sprite> Own(EventNode node)
    {
        foreach (Sprite? sprite in Icons(node.TryCast<TrainingEvent>()?.m_StatIconData))
        {
            yield return sprite;
        }

        if (node.TryCast<FanNumber1Event>() is { } fan)
        {
            if (fan.m_SizeIncreaseIcon != null)
            {
                yield return fan.m_SizeIncreaseIcon;
            }

            if (fan.m_SizeDecreaseIcon != null)
            {
                yield return fan.m_SizeDecreaseIcon;
            }

            foreach (Sprite? sprite in Icons(fan.m_StatIconData))
            {
                yield return sprite;
            }
        }
    }

    /// <summary>A stat icon set's sprites: its default and each stat's.</summary>
    private static IEnumerable<Sprite> Icons(StatIconUIData? icons)
    {
        if (icons == null)
        {
            yield break;
        }

        if (icons._defaultSprite != null)
        {
            yield return icons._defaultSprite;
        }

        Il2CppReferenceArray<StatIconUIData.StatIconAssociation>? pairs = icons._statIconAssociations;
        for (int i = 0; pairs != null && i < pairs.Length; i++)
        {
            if (pairs[i]?.Sprite is { } sprite)
            {
                yield return sprite;
            }
        }
    }

    /// <summary>A sprite's identity besides its name: its texture's name and its rect there.</summary>
    private static (string? Texture, Rect Rect) Identity(Sprite sprite)
    {
        Texture2D? texture = sprite.texture;
        return (texture == null ? null : texture.name, sprite.rect);
    }

    /// <summary>Host: the sources of one set's images (the event's own sprites found once).</summary>
    internal sealed class HostSprites
    {
        private readonly HashSet<IntPtr> _own = new();

        public HostSprites(EventNode node)
        {
            foreach (Sprite sprite in Own(node))
            {
                _own.Add(sprite.Pointer);
            }
        }

        /// <summary>Images that came from their exact source (an object's icon or the event's own).</summary>
        public int Exact { get; private set; }

        /// <summary>Images that go by name, texture and rect only.</summary>
        public int Named { get; private set; }

        /// <summary>
        /// Where <paramref name="sprite"/>, one of a choice's images, came from: the icon of the choice's object
        /// (<paramref name="object1"/>, <paramref name="object2"/>, the free agent <paramref name="agent"/>'s talents and
        /// ability), else the event's own sprites, else none; with its texture and rect.
        /// </summary>
        public ImageSource Source(Sprite sprite, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
        {
            (string? texture, Rect rect) = Identity(sprite);
            ChoiceObjectRef icon = IconOf(sprite.Pointer, object1, object2, agent);
            ImageSource source = !icon.IsNone ? ImageSource.Icon(icon, texture, rect.x, rect.y, rect.width, rect.height)
                : _own.Contains(sprite.Pointer) ? ImageSource.Event(texture, rect.x, rect.y, rect.width, rect.height)
                : ImageSource.Named(texture, rect.x, rect.y, rect.width, rect.height);
            if (source.Kind == ImageSourceKind.Named)
            {
                Named++;
            }
            else
            {
                Exact++;
            }

            return source;
        }

        /// <summary>A preview's icon's source: the event's own sprite, else none (a preview is no object with an icon of its own here).</summary>
        public ImageSource PreviewSource(Sprite sprite)
        {
            (string? texture, Rect rect) = Identity(sprite);
            bool own = _own.Contains(sprite.Pointer);
            if (own)
            {
                Exact++;
            }
            else
            {
                Named++;
            }

            return own ? ImageSource.Event(texture, rect.x, rect.y, rect.width, rect.height) : ImageSource.Named(texture, rect.x, rect.y, rect.width, rect.height);
        }

        private static ChoiceObjectRef IconOf(IntPtr sprite, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
        {
            if (IsIcon(object1?.TryCast<PowerUp>(), sprite))
            {
                return ChoiceObjectRef.Object1;
            }

            if (IsIcon(object2?.TryCast<PowerUp>(), sprite))
            {
                return ChoiceObjectRef.Object2;
            }

            Il2CppList.List<Talent>? talents = agent?.powerups;
            for (int i = 0; talents != null && i < talents.Count && i < FreeAgentData.MaxTalents; i++)
            {
                if (IsIcon(talents[i], sprite))
                {
                    return ChoiceObjectRef.AgentTalent(i);
                }
            }

            return IsIcon(agent?.ability, sprite) ? ChoiceObjectRef.AgentAbility : ChoiceObjectRef.None;
        }

        private static bool IsIcon(PowerUp? power, IntPtr sprite) => power != null && power.icon is { } icon && icon.Pointer == sprite;
    }

    /// <summary>Friend: the host's images for one set, found on this PC (the event's own sprites indexed once, every loaded sprite only if needed).</summary>
    internal sealed class Resolver
    {
        private readonly Dictionary<string, List<Sprite>> _own = new(StringComparer.Ordinal);
        private Dictionary<string, List<Sprite>>? _all;

        public Resolver(EventNode node)
        {
            foreach (Sprite sprite in Own(node))
            {
                Add(_own, sprite);
            }
        }

        /// <summary>Images found by their exact source.</summary>
        public int Exact { get; private set; }

        /// <summary>Images found by their name, texture and rect.</summary>
        public int ByName { get; private set; }

        /// <summary>Images not found here (left out of their card).</summary>
        public int LeftOut { get; private set; }

        /// <summary>For the build's log: "3 image(s) from their exact source, 0 by name, 0 left out", or "" with none.</summary>
        public override string ToString() => Exact + ByName + LeftOut == 0 ? string.Empty : $"{Exact} image(s) from their exact source, {ByName} by name, {LeftOut} left out";

        /// <summary>
        /// The choice's images as this PC's sprites (a null entry: none, or one not found here and left out), or null for
        /// none; over the objects built here for the choice. Never fails: an image not found is left out (logged once).
        /// </summary>
        public Il2CppReferenceArray<Sprite>? Images(EventChoiceData data, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
        {
            if (data.Images == null)
            {
                return null;
            }

            // Finding a sprite allocates (and can make its wrapper anew) after this array is made: each is stored through the
            // write barrier (GameArrays), so the incremental collector can't miss one the array alone holds.
            var images = new Il2CppReferenceArray<Sprite>(data.Images.Count);
            for (int i = 0; i < data.Images.Count; i++)
            {
                if (data.Images[i] is { } name &&
                    Find(name, data.ImageSources != null && i < data.ImageSources.Count ? data.ImageSources[i] : null, object1, object2, agent) is { } sprite)
                {
                    GameArrays.Store(images, i, sprite.Pointer);
                }
            }

            return images;
        }

        /// <summary>A preview's icon (its name and source) as this PC's sprite, or null when it isn't found here (left out, logged once).</summary>
        public Sprite? Icon(string name, ImageSource? source) => Find(name, source, null, null, null);

        private Sprite? Find(string name, ImageSource? source, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
        {
            Sprite? exact = source?.Kind switch
            {
                ImageSourceKind.ObjectIcon => ObjectIcon(source.Target, object1, object2, agent),
                ImageSourceKind.Event => Pick(_own, name, source),
                _ => null,
            };
            if (exact != null && exact.name == name)
            {
                Exact++;
                return exact;
            }

            _all ??= Everything();
            Sprite? named = Pick(_all, name, source);
            if (named != null)
            {
                ByName++;
                if (source is { Kind: not ImageSourceKind.Named } && SaidByName.Count < MaxSaid && SaidByName.Add(name))
                {
                    ModLog.Info($"Menus: EventChoice: the host's image \"{SafeText.Clean(name, NameChars)}\" ({source}) isn't there here; the loaded sprite of its name, texture and rect is shown.");
                }

                return named;
            }

            LeftOut++;
            if (SaidMissing.Count < MaxSaid && SaidMissing.Add(name))
            {
                string why = source == null ? "isn't loaded here, or more than one sprite has its name" : $"({source}) isn't loaded here";
                ModLog.Warn($"Menus: EventChoice: the host's image \"{SafeText.Clean(name, NameChars)}\" {why}; its card shows without it.");
            }

            return null;
        }

        /// <summary>
        /// The sprite of <paramref name="name"/> in <paramref name="index"/> that is the host's: with a source, the first that
        /// agrees on texture and rect (several that do are the same image); without one, the only one of that name.
        /// </summary>
        private static Sprite? Pick(Dictionary<string, List<Sprite>> index, string name, ImageSource? source)
        {
            if (!index.TryGetValue(name, out List<Sprite>? found))
            {
                return null;
            }

            if (source == null)
            {
                return found.Count == 1 ? found[0] : null;
            }

            foreach (Sprite sprite in found)
            {
                (string? texture, Rect rect) = Identity(sprite);
                if (source.SameIdentity(texture, rect.x, rect.y, rect.width, rect.height))
                {
                    return sprite;
                }
            }

            return null;
        }

        private static Sprite? ObjectIcon(ChoiceObjectRef target, ScriptableObject? object1, ScriptableObject? object2, ForwardData? agent)
        {
            PowerUp? power = target.Slot switch
            {
                ChoiceObjectSlot.Object1 => object1?.TryCast<PowerUp>(),
                ChoiceObjectSlot.Object2 => object2?.TryCast<PowerUp>(),
                ChoiceObjectSlot.AgentTalent => agent?.powerups is { } talents && target.Index < talents.Count ? talents[target.Index] : null,
                ChoiceObjectSlot.AgentAbility => agent?.ability,
                _ => null,
            };
            return power?.icon;
        }

        /// <summary>Every loaded sprite, by name (made once per set, only when an image isn't found by its source).</summary>
        private static Dictionary<string, List<Sprite>> Everything()
        {
            var all = new Dictionary<string, List<Sprite>>(StringComparer.Ordinal);
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
            {
                if (found?.TryCast<Sprite>() is { } sprite)
                {
                    Add(all, sprite);
                }
            }

            return all;
        }

        private static void Add(Dictionary<string, List<Sprite>> index, Sprite sprite)
        {
            if (sprite.name is not { Length: > 0 } name)
            {
                return;
            }

            if (!index.TryGetValue(name, out List<Sprite>? list))
            {
                index[name] = list = new List<Sprite>(1);
            }

            if (!list.Exists(s => s.Pointer == sprite.Pointer))
            {
                list.Add(sprite);
            }
        }
    }
}
#endif
