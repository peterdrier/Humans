using Microsoft.EntityFrameworkCore;
using Humans.Tickets.Data.Configurations;
using Humans.Tickets.Domain;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Data;

internal sealed class TicketsDbContext(DbContextOptions<TicketsDbContext> options)
    : DbContext(options)
{
    public DbSet<TicketOrder> TicketOrders => Set<TicketOrder>();
    public DbSet<TicketAttendee> TicketAttendees => Set<TicketAttendee>();
    public DbSet<TicketSyncState> TicketSyncStates => Set<TicketSyncState>();
    public DbSet<TicketTransferRequest> TicketTransferRequests => Set<TicketTransferRequest>();
    public DbSet<EventParticipation> EventParticipations => Set<EventParticipation>();
    public DbSet<TicketSalesEvent> TicketSalesEvents => Set<TicketSalesEvent>();
    public DbSet<TicketCheckoutOrder> TicketCheckoutOrders => Set<TicketCheckoutOrder>();
    public DbSet<TicketCheckoutRefund> TicketCheckoutRefunds => Set<TicketCheckoutRefund>();
    public DbSet<TicketCheckoutOrderEvent> TicketCheckoutOrderEvents => Set<TicketCheckoutOrderEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TicketOrderConfiguration());
        builder.ApplyConfiguration(new TicketAttendeeConfiguration());
        builder.ApplyConfiguration(new TicketSyncStateConfiguration());
        builder.ApplyConfiguration(new TicketTransferRequestConfiguration());
        builder.ApplyConfiguration(new EventParticipationConfiguration());
        builder.ApplyConfiguration(new TicketSalesEventConfiguration());
        builder.ApplyConfiguration(new TicketCheckoutOrderConfiguration());
        builder.ApplyConfiguration(new TicketCheckoutRefundConfiguration());
        builder.ApplyConfiguration(new TicketCheckoutOrderEventConfiguration());
    }
}
