using AwesomeAssertions;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Tickets.Tests.Data;

public sealed class TicketCheckoutRepositoryTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AddOrderAsync_InitialTicket_CommitsTogether(bool reject)
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new RejectTicketSave(reject))
            .Options;
        var factory = new TestDbContextFactory<TicketsDbContext>(options);
        var repository = new TicketCheckoutRepository(factory);
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), State = TicketCheckoutOrderState.Paid };
        var ticket = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Kind = TicketHolderKind.Companion,
            Barcode = "HCOMPANION",
        };
        var save = () => repository.AddOrderAsync(order, Xunit.TestContext.Current.CancellationToken, ticket);

        if (reject)
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket save failed");
        else
            await save();

        await using var db = new TicketsDbContext(options);
        (await db.TicketCheckoutOrders.AnyAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(!reject);
        (await db.TicketCheckoutTickets.AnyAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(!reject);
    }

    private sealed class RejectTicketSave(bool reject) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (reject && eventData.Context!.ChangeTracker.Entries<TicketCheckoutTicket>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Ticket save failed");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
