using AwesomeAssertions;
using Humans.Tickets.Contracts;
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

    [HumansFact]
    public async Task UpdateOfferAsync_DeliveryMetadata_PersistsThenErases()
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        var holder = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            HolderUserId = holder,
            Invites = [new TicketResaleInvite { Email = "invite@example.com", TokenHash = "hash", DeliveryId = deliveryId, DeliveryPending = true }],
        };
        await repository.AddOfferAsync(offer, Xunit.TestContext.Current.CancellationToken);
        offer.Invites.Single().DeliveryPending = false;

        await repository.UpdateOfferAsync(offer, Xunit.TestContext.Current.CancellationToken);

        var stored = (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!;
        stored.Invites.Should().ContainSingle().Which.DeliveryId.Should().Be(deliveryId);
        stored.Invites.Single().DeliveryPending.Should().BeFalse();
        await repository.ErasePiiForUserAsync(holder, [], Xunit.TestContext.Current.CancellationToken);
        (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!.Invites.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UpdateTicketAsync_VoidGraph_CommitsTogether(bool reject)
    {
        var interceptor = new RejectTicketUpdate();
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptor).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        var adult = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            Barcode = "ADULT",
            State = TicketCheckoutTicketState.Offered,
            VoidRefundId = Guid.NewGuid(),
            VoidRefundMinor = 5000,
            VoidRequestedReason = "Returned"
        };
        var child = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            Barcode = "CHILD",
            ParentTicketId = adult.Id,
            State = TicketCheckoutTicketState.Valid
        };
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketId = adult.Id,
            State = TicketResaleOfferState.Open,
            Invites = [new TicketResaleInvite { Email = "invited@example.com", TokenHash = "hash" }]
        };
        await using (var db = new TicketsDbContext(options))
        {
            db.TicketCheckoutTickets.AddRange(adult, child);
            db.TicketResaleOffers.Add(offer);
            await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }
        adult.State = child.State = TicketCheckoutTicketState.Void;
        adult.VoidRefundId = null;
        adult.VoidRefundMinor = null;
        adult.VoidRequestedReason = null;
        offer.State = TicketResaleOfferState.Withdrawn;
        offer.Invites = [];
        interceptor.Reject = reject;
        var save = () => repository.UpdateTicketAsync(adult, Xunit.TestContext.Current.CancellationToken, [child], offer);
        if (reject)
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket update failed");
        else
            await save();

        var storedAdult = (await repository.GetTicketAsync(adult.Id, Xunit.TestContext.Current.CancellationToken))!;
        storedAdult.State.Should().Be(reject ? TicketCheckoutTicketState.Offered : TicketCheckoutTicketState.Void);
        storedAdult.VoidRefundMinor.Should().Be(reject ? 5000 : null);
        (await repository.GetTicketAsync(child.Id, Xunit.TestContext.Current.CancellationToken))!.State
            .Should().Be(reject ? TicketCheckoutTicketState.Valid : TicketCheckoutTicketState.Void);
        var storedOffer = (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!;
        storedOffer.State.Should().Be(reject ? TicketResaleOfferState.Open : TicketResaleOfferState.Withdrawn);
        storedOffer.Invites.Should().HaveCount(reject ? 1 : 0);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UpdateOrderAsync_RenamedTicket_CommitsTogether(bool reject)
    {
        var interceptor = new RejectTicketUpdate();
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptor).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        var original = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, HolderName = "Original", HolderUserId = original }],
        };
        var ticket = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Barcode = "RENAME",
            HolderName = "Original",
            HolderUserId = original,
        };
        await repository.AddOrderAsync(order, Xunit.TestContext.Current.CancellationToken, ticket);
        ticket.HolderName = order.Lines[0].HolderName = "Renamed";
        ticket.HolderUserId = order.Lines[0].HolderUserId = renamed;
        interceptor.Reject = reject;
        var save = () => repository.UpdateOrderAsync(order, Xunit.TestContext.Current.CancellationToken, ticket);
        if (reject)
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket update failed");
        else
            await save();

        var storedOrder = (await repository.GetOrderAsync(order.Id, Xunit.TestContext.Current.CancellationToken))!;
        var storedTicket = (await repository.GetTicketAsync(ticket.Id, Xunit.TestContext.Current.CancellationToken))!;
        storedTicket.HolderName.Should().Be(reject ? "Original" : "Renamed");
        storedTicket.HolderUserId.Should().Be(reject ? original : renamed);
        storedOrder.Lines[0].HolderName.Should().Be(storedTicket.HolderName);
        storedOrder.Lines[0].HolderUserId.Should().Be(storedTicket.HolderUserId);
    }

    [HumansFact]
    public async Task UpdateOfferAsync_SoldNoticeIntent_PersistsThenErases()
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        var holder = Guid.NewGuid();
        var offer = new TicketResaleOffer { Id = Guid.NewGuid(), HolderUserId = holder, SoldNotificationPending = true };
        await repository.AddOfferAsync(offer, Xunit.TestContext.Current.CancellationToken);
        (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!.SoldNotificationPending.Should().BeTrue();
        offer.SoldNotificationPending = false;
        await repository.UpdateOfferAsync(offer, Xunit.TestContext.Current.CancellationToken);
        (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!.SoldNotificationPending.Should().BeFalse();

        await repository.ErasePiiForUserAsync(holder, [], Xunit.TestContext.Current.CancellationToken);

        (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken))!.SoldNotificationPending.Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AddOfferAsync_OfferedTicket_CommitsTogether(bool reject)
    {
        var interceptor = new RejectTicketUpdate();
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptor).Options;
        var repository = new TicketCheckoutRepository(new TestDbContextFactory<TicketsDbContext>(options));
        var ticket = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            Barcode = "HOFFER",
            State = TicketCheckoutTicketState.Valid,
        };
        await using (var db = new TicketsDbContext(options))
        {
            db.TicketCheckoutTickets.Add(ticket);
            await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }
        var offer = new TicketResaleOffer { Id = Guid.NewGuid(), TicketId = ticket.Id, State = TicketResaleOfferState.Open };
        ticket.State = TicketCheckoutTicketState.Offered;
        interceptor.Reject = reject;
        var save = () => repository.AddOfferAsync(offer, Xunit.TestContext.Current.CancellationToken, ticket);
        if (reject)
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ticket update failed");
        else
            await save();

        (await repository.GetOfferAsync(offer.Id, Xunit.TestContext.Current.CancellationToken) is not null).Should().Be(!reject);
        (await repository.GetTicketAsync(ticket.Id, Xunit.TestContext.Current.CancellationToken))!.State
            .Should().Be(reject ? TicketCheckoutTicketState.Valid : TicketCheckoutTicketState.Offered);
    }

    private sealed class RejectTicketUpdate : SaveChangesInterceptor
    {
        public bool Reject { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Reject && eventData.Context!.ChangeTracker.Entries<TicketCheckoutTicket>().Any(e => e.State == EntityState.Modified))
                throw new InvalidOperationException("Ticket update failed");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
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
