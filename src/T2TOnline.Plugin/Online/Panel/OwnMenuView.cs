#if HAS_INTEROP
namespace T2TOnline.Plugin.Online.Panel;

/// <summary>
/// A friend's own game menus as the panel shows them (OnlineSession.Remap.cs): whether they can be opened now, whether one
/// is open (the panel's button then closes it), and a line saying what's going on.
/// </summary>
/// <param name="Available">True while joined as a friend (or in the one-PC test), so "Change my controls" and "Your game's settings" work.</param>
/// <param name="Open">True while one of them is open (or opening, or closing).</param>
/// <param name="Line">What's going on, or how they work; "" when there's nothing to show.</param>
internal readonly record struct OwnMenuView(bool Available, bool Open, string Line)
{
    /// <summary>Nothing to show: not in a friend's game.</summary>
    public static OwnMenuView None => new(false, false, string.Empty);
}
#endif
