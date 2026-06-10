using AwesomeAssertions;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketAttendeeOwnershipTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    [HumansFact]
    public void CurrentOwner_PrefersAttendeeMatchedUserId_WhenSet()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().Be(UserB);
    }

    [HumansFact]
    public void CurrentOwner_ReturnsNull_WhenAttendeeUnmatched_EvenIfOrderHasBuyer()
    {
        // Buyer-fallback removed in nobodies-collective/Humans#856.
        // An unmatched attendee has no owner regardless of who bought the order.
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().BeNull();
    }

    [HumansFact]
    public void CurrentOwner_ReturnsNull_WhenBothUnmatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = null },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().BeNull();
    }

    [HumansFact]
    public void CurrentOwner_ReturnsNull_WhenOrderNavigationMissing()
    {
        var attendee = new TicketAttendee { MatchedUserId = null, TicketOrder = null! };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().BeNull();
    }

    [HumansFact]
    public void IsCurrentOwner_True_WhenAttendeeMatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserB).Should().BeTrue();
        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserA).Should().BeFalse();
    }

    [HumansFact]
    public void IsCurrentOwner_False_ForOrderBuyer_WhenAttendeeUnmatched()
    {
        // Buyer-fallback removed in nobodies-collective/Humans#856.
        // A buyer is not the owner of an attendee with no MatchedUserId.
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserA).Should().BeFalse();
        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserB).Should().BeFalse();
    }
}
