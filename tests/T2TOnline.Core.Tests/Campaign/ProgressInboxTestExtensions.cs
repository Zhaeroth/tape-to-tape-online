using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// An honest host's messages, as the delivery tests send them: each is for the hosting session the friend was welcomed
/// into (the expected session is the message's own). <c>HostTrustTests</c> tests the inbox with another one.
/// </summary>
internal static class ProgressInboxTestExtensions
{
    /// <summary>Offers <paramref name="message"/> as the connection's own hosting session's.</summary>
    public static ProgressOffer OfferOwn(this ProgressInbox inbox, ProgressMessage message) => inbox.Offer(message, message.Session);

    /// <summary>Gives <paramref name="status"/> as the connection's own hosting session's.</summary>
    public static bool StatusOwn(this ProgressInbox inbox, ProgressStatus status) => inbox.Status(status, status.Session);
}
