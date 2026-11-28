using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services;

public sealed class TicketEmailLockTests
{
    [HumansFact]
    public async Task LocksTheHumansMatchedBuyerAndAttendeeAddressesOnly()
    {
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var tickets = Substitute.For<ITicketService>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TicketOrderInfo>>(
        [
            Order(userId, "buyer@example.com", Attendee(userId, "attendee@example.com"), Attendee(otherId, "friend@example.com")),
            Order(otherId, "other-buyer@example.com", Attendee(userId, "second@example.com")),
        ]));

        var locked = await new TicketEmailLock(tickets).GetLockedAddressesAsync(userId, TestContext.Current.CancellationToken);

        locked.Should().BeEquivalentTo(["buyer@example.com", "attendee@example.com", "second@example.com"]);
    }

    private static TicketOrderInfo Order(Guid buyerId, string buyerEmail, params TicketAttendeeInfo[] attendees) => new(
        Guid.NewGuid(), buyerEmail, 100m, TicketPaymentStatus.Paid, "event",
        Instant.MinValue, buyerId, true, attendees);

    private static TicketAttendeeInfo Attendee(Guid userId, string email) => new(
        Guid.NewGuid(), "Attendee", email, "Full Week", TicketAttendeeStatus.Valid, userId);
}
