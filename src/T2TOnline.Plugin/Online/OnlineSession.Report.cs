#if HAS_INTEROP
namespace T2TOnline.Plugin.Online;

// The problem report's offer at the title (NativeUi/ReportOfferWatch.cs, D140): whether the session that just ended went off after an error.
internal sealed partial class OnlineSession
{
    /// <summary>True once online went off after an error in this session (it stays off until the game restarts).</summary>
    public bool Broken => _broken;
}
#endif
