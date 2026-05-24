using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class HasTicketAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHolders()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var audience = NewAudience(ticketHolders: [userA, userB]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoTicketHolders_ReturnsEmpty()
    {
        var audience = NewAudience(ticketHolders: []);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("has-ticket");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Ticket");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static HasTicketAudience NewAudience(HashSet<Guid> ticketHolders)
    {
        var tickets = Substitute.For<ITicketService>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(OrdersForTicketHolders(ticketHolders));

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());

        return new HasTicketAudience(tickets, users);
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
}
