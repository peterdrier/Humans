using Humans.Email.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.MailerLite.Tests.Infrastructure;

/// <summary>
/// An <see cref="ICommunicationPreferenceService"/> stub whose Marketing bulk read returns the
/// given preferences; a user left out has no Marketing preference recorded.
/// </summary>
internal static class MarketingPreferenceStubs
{
    public static ICommunicationPreferenceService For(params (Guid UserId, bool OptedOut)[] marketing)
    {
        var prefs = Substitute.For<ICommunicationPreferenceService>();
        IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot> snapshots = marketing.ToDictionary(
            m => m.UserId,
            m => new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, m.OptedOut, InboxEnabled: true, "Test", Instant.FromUnixTimeSeconds(0)));
        prefs.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Arg.Any<CancellationToken>())
            .Returns(snapshots);
        return prefs;
    }
}
