using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The ticketing team's reads over the checkout lane: lookup across events, the refund list and the sales report.
/// Writes stay on <see cref="ITicketCheckoutService"/>. Authorization is the controllers' job.
/// </summary>
internal interface ITicketSupportService : IApplicationService
{
    /// <summary>Newest first, across every event; empty below <see cref="TicketSupportReports.MinimumQueryLength"/>.</summary>
    Task<IReadOnlyList<OrderSearchHit>> SearchOrdersAsync(string query, CancellationToken ct = default);

    /// <summary>Null when the sales event does not exist.</summary>
    Task<IReadOnlyList<RefundListRow>?> GetRefundsAsync(Guid salesEventId, RefundState? state, CancellationToken ct = default);

    /// <summary>Null when the sales event does not exist.</summary>
    Task<SalesReport?> GetSalesReportAsync(Guid salesEventId, CancellationToken ct = default);
}

internal sealed record OrderSearchHit(TicketCheckoutOrder Order, string EventName);

internal sealed class TicketSupportService(ITicketCheckoutRepository repository, ISettingsService settings)
    : ITicketSupportService
{
    /// <summary>Enough for a person to scan; a broader query should be narrowed, not paged.</summary>
    internal const int MaxHits = 100;

    public async Task<IReadOnlyList<OrderSearchHit>> SearchOrdersAsync(string query, CancellationToken ct = default)
    {
        if (query.Trim().Length < TicketSupportReports.MinimumQueryLength)
            return [];

        var hits = new List<OrderSearchHit>();
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            var name = (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName
                ?? salesEvent.EventSettingsId.ToString();
            hits.AddRange((await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct))
                .Where(o => TicketSupportReports.Matches(o, query))
                .Select(o => new OrderSearchHit(o, name)));
        }
        return hits.OrderByDescending(h => h.Order.CreatedAt).Take(MaxHits).ToList();
    }

    public async Task<IReadOnlyList<RefundListRow>?> GetRefundsAsync(
        Guid salesEventId, RefundState? state, CancellationToken ct = default)
    {
        if (await repository.GetSalesEventAsync(salesEventId, ct) is null)
            return null;
        var orders = await repository.GetOrdersForSalesEventAsync(salesEventId, ct);
        var refunds = await repository.GetRefundsForSalesEventAsync(salesEventId, ct);
        return TicketSupportReports.RefundList(orders, refunds, state);
    }

    public async Task<SalesReport?> GetSalesReportAsync(Guid salesEventId, CancellationToken ct = default)
    {
        var salesEvent = await repository.GetSalesEventAsync(salesEventId, ct);
        if (salesEvent is null)
            return null;
        var zone = (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.TimeZone ?? DateTimeZone.Utc;
        var orders = await repository.GetOrdersForSalesEventAsync(salesEventId, ct);
        var refunds = await repository.GetRefundsForSalesEventAsync(salesEventId, ct);
        return TicketSupportReports.Sales(orders, refunds, zone);
    }
}
