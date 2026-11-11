#if HAS_INTEROP
using System.Globalization;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// Signal bars: three of the game's <c>pip-square</c> at rising heights (the game has no such icon). A count of bars first and a colour second (XAG 103), and the measured
/// round trip beside them only when asked (on focus). With nothing measured, nothing shows: absent stays absent.
/// </summary>
internal sealed class SignalBarsView
{
    private const float BarWidth = 9;
    private const float BarGap = 4;
    private static readonly float[] Heights = { 10, 18, 26 };

    private readonly RectTransform _root;
    private readonly Image[] _bars = new Image[SignalBars.Most];
    private readonly TextMeshProUGUI _ms;

    /// <summary>Makes one: the bars' bottom left corner at the root's, the ms label to their right.</summary>
    public SignalBarsView(Transform parent, float textSize = 26)
    {
        _root = LobbyUi.New(parent, "Signal");
        Sprite? pip = NativeArt.Get(NativeArt.Pip);
        for (int i = 0; i < _bars.Length; i++)
        {
            Image bar = LobbyUi.Picture(_root, $"Bar{i}", pip, LobbyUi.White, sliced: false);
            bar.preserveAspect = false;
            LobbyUi.At(bar.rectTransform, 0, 0, i * (BarWidth + BarGap), 0, BarWidth, Heights[i], 0, 0);
            _bars[i] = bar;
        }

        _ms = LobbyUi.Label(LobbyFont.Body, _root, "Ms", textSize);
        LobbyUi.At(_ms.rectTransform, 0, 0, Width + 8, -6, 120, 36, 0, 0);
        LobbyUi.Show(_ms, false);
    }

    /// <summary>The bars' own width.</summary>
    public const float Width = (SignalBars.Most * BarWidth) + ((SignalBars.Most - 1) * BarGap);

    /// <summary>Its root: place it (a box of <see cref="Width"/> by 26).</summary>
    public RectTransform Root => _root;

    /// <summary>Shows a measurement: <paramref name="bars"/> 0 hides it all. <paramref name="showMs"/>: the number too.</summary>
    public void Show(int bars, int pingMs, bool showMs)
    {
        bool any = bars > 0;
        LobbyUi.Show(_root, any);
        if (!any)
        {
            return;
        }

        Color lit = bars >= 3 ? LobbyUi.Green : bars == 2 ? LobbyUi.Gold : LobbyUi.Pink;
        var unlit = new Color(1, 1, 1, 0.22f);
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].color = i < bars ? lit : unlit;
        }

        LobbyUi.Show(_ms, showMs && pingMs >= 0); // measured by the host; an estimate never gets a number
        if (showMs && pingMs >= 0)
        {
            LobbyUi.Set(_ms, pingMs.ToString(CultureInfo.InvariantCulture) + " ms");
        }
    }
}
#endif
