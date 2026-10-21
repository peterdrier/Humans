using Humans.Email.Contracts;
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
            users: [User(optInNoTicket), User(optInWithTicket), User(optedOut), User(noPrefRow)],
            ticketHolders: [optInWithTicket],
            (optInNoTicket, false), (optInWithTicket, false), (optedOut, true));

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
    }

    private static MarketingNoTicketAudience NewAudience(
        IReadOnlyList<UserInfo> users,
        HashSet<Guid> ticketHolders,
        params (Guid UserId, bool OptedOut)[] marketing)
    {
        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);

        var ticketService = Substitute.For<ITicketService>();
        ticketService.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(OrdersForTicketHolders(ticketHolders));

        return new MarketingNoTicketAudience(userService, ticketService, MarketingPreferenceStubs.For(marketing));
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

    private static UserInfo User(Guid userId) =>
        UserInfoStubHelpers.MakeUserInfo(userId);
}
