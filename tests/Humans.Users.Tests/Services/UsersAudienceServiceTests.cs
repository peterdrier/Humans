using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Services;

public class UsersAudienceServiceTests
{
    [HumansFact]
    public async Task AudienceYears_IncludeMatchedAttendeesWithoutMatchedBuyers()
    {
        var attendeeId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var users = Substitute.For<IUserManagement>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>
        {
            UserInfoStubHelpers.FromEntities(new User { Id = attendeeId }, new Profile { UserId = attendeeId }),
            UserInfoFixtures.FromUser(new User { Id = buyerId }),
            UserInfoFixtures.FromUser(new User { Id = Guid.NewGuid() }),
        });
        var tickets = Substitute.For<ITicketService>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).Returns(new List<TicketOrderInfo>
        {
            new(Guid.NewGuid(), "attendee-only", null, null, 100m, "EUR", null,
                TicketPaymentStatus.Paid, "event", Instant.FromUtc(2025, 1, 1, 0, 0), null, false,
                [new(Guid.NewGuid(), "ticket", null, null, null, 100m, TicketAttendeeStatus.Valid, attendeeId)]),
            new(Guid.NewGuid(), "buyer", null, null, 100m, "EUR", null,
                TicketPaymentStatus.Paid, "event", Instant.FromUtc(2026, 1, 1, 0, 0), buyerId, true, []),
            new(Guid.NewGuid(), "unmatched", null, null, 100m, "EUR", null,
                TicketPaymentStatus.Paid, "event", Instant.FromUtc(2024, 1, 1, 0, 0), null, false, []),
        });
        var service = new UsersAudienceService(users, tickets);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var allTime = await service.GetAudienceSegmentationAsync(null, ct);
        allTime.AvailableYears.Should().BeEquivalentTo(new[] { 2025, 2026 });
        allTime.WithTicket.Should().Be(2);
        allTime.WithBoth.Should().Be(1);
        allTime.WithNeither.Should().Be(1);

        var selected = await service.GetAudienceSegmentationAsync(2025, ct);
        selected.AvailableYears.Should().BeEquivalentTo(allTime.AvailableYears);
        selected.SelectedYear.Should().Be(2025);
        selected.WithTicket.Should().Be(1);
        selected.WithBoth.Should().Be(1);
        selected.WithNeither.Should().Be(2);
    }
}
