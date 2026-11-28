using Humans.Base.Extensions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Microsoft.Extensions.Hosting;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed class TicketReconService(
    ITicketCheckoutRepository repository,
    IEnumerable<ITicketingPlatform> platforms,
    IHostEnvironment environment,
    IClock clock,
    ILogger<TicketReconService> logger) : ITicketReconService
{
    private readonly IReadOnlyList<ITicketingPlatform> _platforms = platforms.ToList();

    public IReadOnlyList<PlatformInfo> Platforms =>
        _platforms.Select(b => new PlatformInfo(b.Key, b.IsConfigured, b.Setup)).ToList();

    public async Task<IReadOnlyList<PlatformFinding>?> CheckPlatformAsync(
        string key, string? platformEventRef, CancellationToken ct = default) =>
        PlatformFor(key) is { } platform
            ? await platform.CheckAsync(string.IsNullOrWhiteSpace(platformEventRef) ? null : platformEventRef.Trim(), ct)
            : null;

    private ITicketingPlatform? PlatformFor(string key) =>
        _platforms.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    public async Task<IReadOnlyList<string>> GetPlatformsInUseAsync(CancellationToken ct = default)
    {
        // An order keeps its platform when its event's ticketing settings move on, so a platform with orders stays in use.
        var allSettings = await repository.ListTicketingSettingsAsync(ct);
        return allSettings.Select(e => e.PlatformKey)
            .Concat((await AllOrdersAsync(allSettings, ct)).Select(o => o.PlatformKey))
            .Distinct(StringComparer.Ordinal).Where(key => PlatformFor(key) is not null).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>QA, previews and dev share the platforms' test accounts, so each sees the others' orders.</summary>
    private bool SharedAccount => !environment.IsProduction();

    public async Task<TicketRecon?> ReconcilePlatformAsync(
        string platformKey, Instant from, Instant to, CancellationToken ct = default)
    {
        if (PlatformFor(platformKey) is not { } platform)
            return null;
        if (!platform.IsConfigured)
            return TicketReconBuilder.Unreadable(platformKey, from, to, $"{platformKey} is not configured, so it cannot be read.");

        var allSettings = await repository.ListTicketingSettingsAsync(ct);
        var orders = await AllOrdersAsync(allSettings, ct);

        var platformOrders = new List<PlatformOrderSummary>();
        string? eventRef = null;
        try
        {
            // Every event the platform's orders were sold under, and every event whose ticketing settings sell through it now.
            var eventRefs = orders
                .Where(o => string.Equals(o.PlatformKey, platformKey, StringComparison.Ordinal) && o.PlatformOrderId is not null)
                .Select(o => platform.EventRefOf(o.PlatformOrderId!))
                .Concat(allSettings
                    .Where(e => string.Equals(e.PlatformKey, platformKey, StringComparison.Ordinal))
                    .Select(e => string.IsNullOrWhiteSpace(e.PlatformEventRef) ? null : e.PlatformEventRef.Trim()))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var each in eventRefs)
            {
                eventRef = each;
                platformOrders.AddRange(await platform.ListOrdersAsync(
                    each, from - TicketReconBuilder.Margin, to + TicketReconBuilder.Margin, ct));
            }
        }
        catch (Exception ex) when (ex is TicketingPlatformException or HttpRequestException or ArgumentException
                                       or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Reconciliation could not read {Platform} event {EventRef}", platformKey, eventRef);
            return TicketReconBuilder.Unreadable(platformKey, from, to,
                $"{platformKey}{(eventRef is null ? "" : $" event {eventRef}")} could not be read: {ex.Message}");
        }

        var tickets = new List<TicketCheckoutTicket>();
        foreach (var settingsId in orders
                     .Where(o => string.Equals(o.PlatformKey, platformKey, StringComparison.Ordinal) && o.CreatedAt >= from && o.CreatedAt < to)
                     .Select(o => o.TicketingSettingsId).Distinct())
            tickets.AddRange(await repository.GetTicketsForSettingsAsync(settingsId, ct));

        return TicketReconBuilder.Build(platformKey, from, to, orders, platformOrders, tickets, SharedAccount);
    }

    public async Task<TicketPaymentRecon?> ReconcilePaymentsAsync(
        string platformKey, Instant from, Instant to, CancellationToken ct = default)
    {
        if (PlatformFor(platformKey) is not { IsConfigured: true } platform)
            return null;

        var orders = await AllOrdersAsync(await repository.ListTicketingSettingsAsync(ct), ct);
        PaymentLedger? ledger;
        IReadOnlyCollection<Guid>? returnedLater = null;
        try
        {
            ledger = await platform.GetPaymentLedgerAsync(from, to, ct);
            var now = clock.GetCurrentInstant();
            if (ledger is not null && to < now && TicketPaymentReconBuilder.AwaitsReturn(ledger, orders, platformKey)
                && await platform.GetPaymentLedgerAsync(to, now, ct) is { } later)
                returnedLater = TicketPaymentReconBuilder.Returned(later);
        }
        catch (Exception ex) when (ex is TicketingPlatformException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Payment reconciliation could not read {Platform}", platformKey);
            return TicketPaymentReconBuilder.Unreadable(platformKey, from, to,
                $"{platformKey}'s payment account could not be read: {ex.Message}");
        }

        if (ledger is null)
            return null;
        return TicketPaymentReconBuilder.Build(platformKey, from, to, ledger, orders, returnedLater, SharedAccount);
    }

    /// <summary>Three reconcile runs past expiry: by then the reconcile should have settled the order either way.</summary>
    private static readonly Duration StuckGrace = Duration.FromMinutes(15);

    public async Task<PlatformLedger?> GetPlatformLedgerAsync(string platformKey, CancellationToken ct = default)
    {
        var info = Platforms.FirstOrDefault(b => string.Equals(b.Key, platformKey, StringComparison.Ordinal));
        if (info is null)
            return null;

        var now = clock.GetCurrentInstant();
        var orders = (await AllOrdersAsync(await repository.ListTicketingSettingsAsync(ct), ct))
            .Where(o => string.Equals(o.PlatformKey, platformKey, StringComparison.Ordinal)).ToList();
        var refunds = new List<TicketCheckoutRefund>();
        foreach (var settingsId in orders.Select(o => o.TicketingSettingsId).Distinct())
            refunds.AddRange(await repository.GetRefundsForSettingsAsync(settingsId, ct));
        var orderIds = orders.Select(o => o.Id).ToHashSet();

        var stuck = orders
            .Where(o => o.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && o.ExpiresAt + StuckGrace < now)
            .ToList();
        // Reconciliation's paid side: a free order never reached the platform, so it is neither side's.
        var paid = orders
            .Where(o => o.State == TicketCheckoutOrderState.Paid && !(o.TotalMinor == 0 && o.PlatformOrderId is null))
            .ToList();
        var mine = refunds.Where(r => orderIds.Contains(r.OrderId)).ToList();
        var unsettled = mine.Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).ToList();
        // A failed refund is terminal and a retry is a new row, so it matters only until a later attempt on the order goes through.
        var failed = mine
            .Where(r => r.State == RefundState.Failed
                && !mine.Any(later => later.OrderId == r.OrderId && later.CreatedAt > r.CreatedAt && later.State != RefundState.Failed))
            .ToList();
        return new PlatformLedger(info, orders, paid, stuck, unsettled, failed);
    }

    private async Task<List<TicketCheckoutOrder>> AllOrdersAsync(IReadOnlyList<TicketingSettings> allSettings, CancellationToken ct)
    {
        var orders = new List<TicketCheckoutOrder>();
        foreach (var ticketing in allSettings)
            orders.AddRange(await repository.GetOrdersForSettingsAsync(ticketing.Id, ct));
        return orders;
    }
}
