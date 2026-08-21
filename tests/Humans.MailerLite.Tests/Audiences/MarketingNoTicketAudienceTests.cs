using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.MailerLite.Tests.Infrastructure;
using Humans.MailerLite.Services.Audiences;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

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

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([optInNoTicket]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience([], []);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

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

        var ticketService = Substitute.For<ITicketServiceRead>();
        ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(OrdersForTicketHolders(ticketHolders));

        return new MarketingNoTicketAudience(userService, ticketService);
    }

    private static IReadOnlyList<TicketOrderInfo> OrdersForTicketHolders(IEnumerable<Guid> userIds) =>
        userIds.Select(userId => new TicketOrderInfo(
            Id: Guid.NewGuid(),
            VendorOrderId: $"ord_{userId:N}",
            BuyerName: null,
            BuyerEmail: null,
            TotalAmount: 0m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "ev_test",
            PurchasedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            MatchedUserId: null,
            IsCurrentEvent: true,
            Attendees: [new TicketAttendeeInfo(
                Id: Guid.NewGuid(),
                VendorTicketId: $"tkt_{userId:N}",
                AttendeeName: null,
                AttendeeEmail: null,
                TicketTypeName: null,
                Price: 0m,
                Status: TicketAttendeeStatus.Valid,
                MatchedUserId: userId)])).ToList();

    private static UserInfo UserWithMarketingPref(Guid userId, bool optedOut) =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = "u", PreferredLanguage = "en" },
            [], [], [], profile: null,
            [UserFixtures.Preference(
                category: MessageCategory.Marketing,
                optedOut: optedOut,
                updatedAt: Instant.FromUnixTimeSeconds(0),
                updateSource: "Test")]);

    private static UserInfo UserWithoutMarketingPref(Guid userId) =>
        UserInfoStubHelpers.MakeUserInfo(userId);
}
