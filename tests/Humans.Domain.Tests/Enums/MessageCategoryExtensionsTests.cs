using AwesomeAssertions;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Domain.Tests.Enums;

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
}
