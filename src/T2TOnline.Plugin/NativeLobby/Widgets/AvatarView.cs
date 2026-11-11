#if HAS_INTEROP
using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// A Steam avatar: a square of the player's colour with their initial at once, replaced in place by the real picture when Steam has it, so a card doesn't pop twice. Rounded and outlined with
/// the game's own panel sprites (a stencil mask). Asks <see cref="AvatarCache"/> when it's shown and when <see cref="AvatarCache.Version"/> changes, never per frame; keeps no texture past that.
/// </summary>
internal sealed class AvatarView
{
    private readonly Image _colour;
    private readonly TextMeshProUGUI _initial;
    private readonly RawImage _picture;
    private readonly Image _dimmer;
    private readonly AvatarSize _size;
    private ulong _steamId;
    private int _version = -1;
    private string _name = string.Empty;

    private AvatarView(RectTransform root, Image colour, TextMeshProUGUI initial, RawImage picture, Image dimmer, AvatarSize size)
    {
        Root = root;
        _colour = colour;
        _initial = initial;
        _picture = picture;
        _dimmer = dimmer;
        _size = size;
    }

    /// <summary>Its root: place it.</summary>
    public RectTransform Root { get; }

    /// <summary>Makes one: its letter's size follows <paramref name="side"/>.</summary>
    public static AvatarView Make(Transform parent, string name, float side, AvatarSize size)
    {
        RectTransform root = LobbyUi.New(parent, name);
        Image mask = root.gameObject.AddComponent(Il2CppType.Of<Image>())?.TryCast<Image>() ?? throw new InvalidOperationException("the avatar has no mask image");
        mask.sprite = NativeArt.Get(NativeArt.PanelMask);
        mask.type = Image.Type.Sliced;
        mask.raycastTarget = false;
        Mask? stencil = root.gameObject.AddComponent(Il2CppType.Of<Mask>())?.TryCast<Mask>();
        if (stencil != null)
        {
            stencil.showMaskGraphic = true;
        }

        Image colour = LobbyUi.Picture(root, "Colour", null, LobbyUi.Well);
        LobbyUi.Fill(colour.rectTransform);
        TextMeshProUGUI initial = LobbyUi.Label(LobbyFont.Title, root, "Initial", Math.Max(26, side * 0.55f), align: TextAlignmentOptions.Center);
        LobbyUi.Fill(initial.rectTransform);
        LobbyUi.ForName(initial);
        RawImage picture = LobbyUi.Raw(root, "Picture");
        LobbyUi.Fill(picture.rectTransform);
        Image dimmer = LobbyUi.Picture(root, "Dimmer", null, new Color(0, 0, 0, 0.55f));
        LobbyUi.Fill(dimmer.rectTransform);
        LobbyUi.Show(dimmer, false);
        LobbyUi.Show(picture, false);
        return new AvatarView(root, colour, initial, picture, dimmer, size);
    }

    /// <summary>Shows a person. The colour and letter come at once; the picture when Steam has it. <paramref name="dim"/>: they aren't here yet.</summary>
    public void Show(ulong steamId, string name, Color colour, bool dim)
    {
        _colour.color = colour;
        LobbyUi.Show(_dimmer, dim);
        if (steamId != _steamId || !string.Equals(name, _name, StringComparison.Ordinal))
        {
            _steamId = steamId;
            _name = name;
            LobbyUi.Set(_initial, InitialOf(name));
            _version = -1;
        }

        Refresh();
    }

    /// <summary>Asks again if the cache has changed. A dictionary lookup and an int compare: fine at a screen's redraw rate.</summary>
    public void Refresh()
    {
        if (_version == AvatarCache.Version)
        {
            return;
        }

        _version = AvatarCache.Version;
        Texture2D? texture = _steamId == 0 ? null : AvatarCache.Get(_steamId, _size);
        _picture.texture = texture;
        LobbyUi.Show(_picture, texture != null);
    }

    /// <summary>The first letter of a name for the coloured square; empty for what the title font can't draw (the square alone then).</summary>
    private string InitialOf(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var runes = name.EnumerateRunes();
            if (!runes.MoveNext() || !runes.Current.IsBmp)
            {
                return string.Empty;
            }

            char first = char.ToUpper((char)runes.Current.Value, CultureInfo.InvariantCulture);
            return _initial.font != null && _initial.font.HasCharacter(first, true, false) ? first.ToString() : string.Empty;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: an avatar's letter failed ({ModLog.SafeErrorText(ex)}).");
            return string.Empty;
        }
    }
}
#endif
