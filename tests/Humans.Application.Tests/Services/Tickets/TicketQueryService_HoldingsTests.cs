using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Unit tests for <see cref="TicketQueryService.GetUserTicketHoldingsAsync"/>.
/// All dependencies are NSubstitute substitutes — no EF context required.
/// </summary>
public sealed class TicketQueryService_HoldingsTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly TicketQueryService Service;

    public TicketQueryService_HoldingsTests()
    {
        _transferRepo.GetByStatusAsync(Arg.Any<TicketTransferStatus>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _transferRepo.GetBySenderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Service = new TicketQueryService(
            _ticketRepo,
            _transferRepo,
            Substitute.For<IBudgetService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IUserEmailService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<IShiftManagementService>(),
            SystemClock.Instance);

        // Default: no orders, no visible attendees
        _ticketRepo.GetOrdersMatchedToUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _ticketRepo.GetAttendeesVisibleToUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [HumansFact]
    public async Task ReturnsEmpty_WhenUserHasNoOrdersAndNoAttendees()
    {
        var result = await Service.GetUserTicketHoldingsAsync(UserA);
        result.OrderCount.Should().Be(0);
        result.Tickets.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CountsOrdersByBuyerAndAttendeeNamesByCurrentOwner()
    {
        // UserA bought 2 orders.
        var order1Id = Guid.NewGuid();
        var order2Id = Guid.NewGuid();
        var order3Id = Guid.NewGuid();

        var order1 = new TicketOrder { Id = order1Id, MatchedUserId = UserA };
        var order2 = new TicketOrder { Id = order2Id, MatchedUserId = UserA };
        var order3 = new TicketOrder { Id = order3Id, MatchedUserId = UserB };

        _ticketRepo.GetOrdersMatchedToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([order1, order2]);

        // Order 1: attendee matched to UserA (counts), attendee unmatched (cascades to order buyer = UserA, counts)
        // Order 2: attendee matched to UserB (does NOT count for UserA)
        // Order 3 (UserB's order): attendee matched to UserA via MatchedUserId (counts for UserA)
        var attendeeMatchedA_Order1 = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Matched-A-Order1",
            MatchedUserId = UserA,
            TicketOrder = order1,
            TicketOrderId = order1Id,
        };
        var attendeeUnmatched_Order1 = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Unmatched-Order1",
            MatchedUserId = null,
            TicketOrder = order1,
            TicketOrderId = order1Id,
        };
        var attendeeMatchedB_Order2 = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Matched-B-Order2",
            MatchedUserId = UserB,
            TicketOrder = order2,
            TicketOrderId = order2Id,
        };
        var attendeeMatchedA_Order3 = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Matched-A-Order3",
            MatchedUserId = UserA,
            TicketOrder = order3,
            TicketOrderId = order3Id,
        };

        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([
                attendeeMatchedA_Order1,
                attendeeUnmatched_Order1,
                attendeeMatchedB_Order2,
                attendeeMatchedA_Order3
            ]);

        var result = await Service.GetUserTicketHoldingsAsync(UserA);

        result.OrderCount.Should().Be(2);
        result.Tickets.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task IncludesVoidedTicketsButSortsThemLast()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder { Id = orderId, MatchedUserId = UserA };

        var voided = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Aaron Voided",
            MatchedUserId = UserA,
            Status = TicketAttendeeStatus.Void,
            TicketOrder = order,
            TicketOrderId = orderId,
        };
        var active = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Zoe Valid",
            MatchedUserId = UserA,
            Status = TicketAttendeeStatus.Valid,
            TicketOrder = order,
            TicketOrderId = orderId,
        };

        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([voided, active]);

        var result = await Service.GetUserTicketHoldingsAsync(UserA);

        result.Tickets.Should().HaveCount(2);
        result.Tickets[0].Status.Should().Be(TicketAttendeeStatus.Valid);
        result.Tickets[1].Status.Should().Be(TicketAttendeeStatus.Void);
    }

    [HumansFact]
    public async Task StampsPendingOutgoingTransferOnTheAffectedRowOnly()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder { Id = orderId, MatchedUserId = UserA };
        var inTransfer = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Ada In-Transfer",
            MatchedUserId = UserA,
            TicketOrder = order,
            TicketOrderId = orderId,
        };
        var untouched = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            AttendeeName = "Zoe Untouched",
            MatchedUserId = UserA,
            TicketOrder = order,
            TicketOrderId = orderId,
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([inTransfer, untouched]);

        var transferId = Guid.NewGuid();
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns([
                new TicketTransferRequest
                {
                    Id = transferId,
                    OriginalTicketAttendeeId = inTransfer.Id,
                    SenderUserId = UserA,
                    Status = TicketTransferStatus.Pending,
                },
                new TicketTransferRequest
                {
                    Id = Guid.NewGuid(),
                    OriginalTicketAttendeeId = untouched.Id,
                    SenderUserId = UserA,
                    Status = TicketTransferStatus.Cancelled,
                },
            ]);

        var result = await Service.GetUserTicketHoldingsAsync(UserA);

        var pendingRow = result.Tickets.Single(t => t.AttendeeId == inTransfer.Id);
        pendingRow.HasPendingOutgoingTransfer.Should().BeTrue();
        pendingRow.PendingTransferRequestId.Should().Be(transferId);
        var cleanRow = result.Tickets.Single(t => t.AttendeeId == untouched.Id);
        cleanRow.HasPendingOutgoingTransfer.Should().BeFalse();
        cleanRow.PendingTransferRequestId.Should().BeNull();
    }
}
