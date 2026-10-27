using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using Xunit;

namespace Humans.Email.Tests.Enums;

public class MessageCategoryExtensionsTests
{
    [HumansTheory]
    [InlineData(MessageCategory.Marketing, true)]
    [InlineData(MessageCategory.System, false)]
    [InlineData(MessageCategory.CampaignCodes, false)]
    [InlineData(MessageCategory.FacilitatedMessages, false)]
    [InlineData(MessageCategory.Ticketing, false)]
    [InlineData(MessageCategory.VolunteerUpdates, false)]
    [InlineData(MessageCategory.TeamUpdates, false)]
    [InlineData(MessageCategory.Governance, false)]
    public void DefaultOptedOut_MatchesDomainDefaults(MessageCategory category, bool expected) =>
        category.DefaultOptedOut().Should().Be(expected);

    [HumansFact]
    public void DefaultOptedOut_OnlyMarketing_AmongActiveCategories() =>
        MessageCategoryExtensions.ActiveCategories
            .Where(c => c.DefaultOptedOut())
            .Should().Equal(MessageCategory.Marketing);

    /// <summary>
    /// <see cref="MessageCategory"/> is stored as a string (<c>email_communication_preferences.Category</c>)
    /// and named in every unsubscribe token's payload: renaming a member orphans stored rows and
    /// invalidates tokens already in members' inboxes.
    /// </summary>
    [HumansFact]
    public void MemberNames_AreStable() =>
        Enum.GetNames<MessageCategory>().Should().Contain(
        [
            "System", "EventOperations", "CommunityUpdates", "Marketing", "Governance",
            "CampaignCodes", "FacilitatedMessages", "Ticketing", "VolunteerUpdates", "TeamUpdates",
        ]);
}
