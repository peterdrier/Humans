using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services;

public sealed class TicketingPreferenceLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansTheory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task OnlyAMatchedTicketHolderLocksTicketing(int orderCount, bool locked)
    {
        var userId = Guid.NewGuid();
        var tickets = Substitute.For<ITicketService>();
        tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(orderCount, []));

        var categories = await new TicketingPreferenceLock(tickets).GetLockedCategoriesAsync(userId, Ct);

        categories.Should().BeEquivalentTo(locked ? [MessageCategory.Ticketing] : Array.Empty<MessageCategory>());
    }

    [HumansFact]
    public async Task AMatchedAttendeeWithoutAnOrderLocksTicketing()
    {
        var userId = Guid.NewGuid();
        var tickets = Substitute.For<ITicketService>();
        tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0,
                [new UserTicketHoldingRow(Guid.NewGuid(), "A", null, "TT-1", "Standard", TicketAttendeeStatus.Valid)]));

        (await new TicketingPreferenceLock(tickets).GetLockedCategoriesAsync(userId, Ct))
            .Should().Equal(MessageCategory.Ticketing);
    }
}
