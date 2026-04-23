using Microsoft.EntityFrameworkCore;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Tickets;

/// <summary>
/// EF-backed implementation of <see cref="ITicketingBudgetRepository"/>. The
/// only non-test file that queries <c>ticket_orders</c>/<c>ticket_attendees</c>
/// on behalf of the Tickets→Budget bridge after the TicketingBudgetService
/// migration lands.
/// </summary>
/// <remarks>
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> itself remains a
/// per-request, short-lived instance — the same pattern as the other §15
/// repositories (Profile, User, etc., per design-rules §15b).
/// </remarks>
public sealed class TicketingBudgetRepository : ITicketingBudgetRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public TicketingBudgetRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<PaidTicketOrderSummary>> GetPaidOrderSummariesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => new PaidTicketOrderSummary(
                o.PurchasedAt,
                o.TotalAmount,
                o.StripeFee,
                o.ApplicationFee,
                o.Attendees.Count(a =>
                    a.Status == TicketAttendeeStatus.Valid ||
                    a.Status == TicketAttendeeStatus.CheckedIn)))
            .ToListAsync(ct);
    }
}
