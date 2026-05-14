using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NSubstitute;
using Xunit;

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
    private readonly TicketQueryService Service;

    public TicketQueryService_HoldingsTests()
    {
        Service = new TicketQueryService(
            _ticketRepo,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IBudgetService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IUserEmailService>(),
            Substitute.For<IProfileService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<IShiftManagementService>(),
            SystemClock.Instance);

        // Default: no orders, no visible attendees
        _ticketRepo.GetOrdersMatchedToUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketOrder>());
        _ticketRepo.GetAttendeesVisibleToUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketAttendee>());
    }

    [HumansFact]
    public async Task ReturnsEmpty_WhenUserHasNoOrdersAndNoAttendees()
    {
        var result = await Service.GetUserTicketHoldingsAsync(UserA);
        result.OrderCount.Should().Be(0);
        result.AttendeeNames.Should().BeEmpty();
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
            .Returns(new[] { order1, order2 });

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
            .Returns(new[]
            {
                attendeeMatchedA_Order1,
                attendeeUnmatched_Order1,
                attendeeMatchedB_Order2,
                attendeeMatchedA_Order3,
            });

        var result = await Service.GetUserTicketHoldingsAsync(UserA);

        result.OrderCount.Should().Be(2);
        result.AttendeeNames.Should().HaveCount(3);
    }
}
