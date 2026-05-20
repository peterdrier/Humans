using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class MarketingNoTicketAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ExcludesTicketHolders_AndNonOptIns()
    {
        var optInNoTicket = Guid.NewGuid();   // OptedOut=false, no ticket   → IN
        var optInWithTicket = Guid.NewGuid(); // OptedOut=false, has ticket  → OUT
        var optedOut = Guid.NewGuid();        // OptedOut=true               → OUT
        var noPrefRow = Guid.NewGuid();       // no row                      → OUT

        var audience = NewAudience(
            users: new[]
            {
                UserWithMarketingPref(optInNoTicket, optedOut: false),
                UserWithMarketingPref(optInWithTicket, optedOut: false),
                UserWithMarketingPref(optedOut, optedOut: true),
                UserWithoutMarketingPref(noPrefRow),
            },
            ticketHolders: [optInWithTicket]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([optInNoTicket]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience([], []);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([], []);
        audience.Key.Should().Be("marketing-no-ticket");
        audience.MailerLiteGroupName.Should().Be("Humans - Marketing no Ticket");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static MarketingNoTicketAudience NewAudience(
        IReadOnlyList<UserInfo> users,
        HashSet<Guid> ticketHolders)
    {
        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);

        var ticketService = Substitute.For<ITicketQueryService>();
        ticketService.GetUserIdsWithTicketsAsync().Returns(ticketHolders);

        return new MarketingNoTicketAudience(userService, ticketService);
    }

    private static UserInfo UserWithMarketingPref(Guid userId, bool optedOut) =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = "u", PreferredLanguage = "en" },
            [], [], [], profile: null, [], [], [],
            [new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = optedOut,
                UpdatedAt = Instant.FromUnixTimeSeconds(0),
                UpdateSource = "Test",
            }]);

    private static UserInfo UserWithoutMarketingPref(Guid userId) =>
        UserInfoStubHelpers.MakeUserInfo(userId);
}
