using AwesomeAssertions;
using Humans.MailerLite.Services.Audiences;
using Humans.MailerLite.Tests.Infrastructure;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Audiences;

public class HasTicketAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHolders()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var audience = NewAudience(ticketHolders: [userA, userB]);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoTicketHolders_ReturnsEmpty()
    {
        var audience = NewAudience(ticketHolders: []);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("has-ticket");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Ticket");
    }

    private static HasTicketAudience NewAudience(HashSet<Guid> ticketHolders)
    {
        var tickets = Substitute.For<ITicketService>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(OrdersForTicketHolders(ticketHolders));

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());

        return new HasTicketAudience(tickets, users, MarketingPreferenceStubs.For());
    }

    private static IReadOnlyList<TicketOrderInfo> OrdersForTicketHolders(IEnumerable<Guid> userIds) =>
        userIds.Select(userId => new TicketOrderInfo(
            Id: Guid.NewGuid(),
            BuyerEmail: null,
            TotalAmount: 0m,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "ev_test",
            PurchasedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            MatchedUserId: null,
            IsCurrentEvent: true,
            Attendees: [new TicketAttendeeInfo(
                Id: Guid.NewGuid(),
                AttendeeName: null,
                AttendeeEmail: null,
                TicketTypeName: null,
                Status: TicketAttendeeStatus.Valid,
                MatchedUserId: userId)])).ToList();
}
