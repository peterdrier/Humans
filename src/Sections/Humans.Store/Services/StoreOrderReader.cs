using System.Globalization;
using System.Resources;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreOrderReader(
    IStoreRepository repo,
    ICampService campService,
    ITeamService teamService,
    IClock clock,
    ISettingsService settingsService) : IApplicationService
{
    private static readonly ResourceManager Resources = new(typeof(StoreResource));

    /// <summary>Falls back to the current UTC year before an active event exists.</summary>
    public async Task<int> GetCurrentEventYearAsync(CancellationToken ct = default)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }

    /// <summary>
    /// Includes deactivated products. The org runs one event year, so the active year's catalog drives repricing rather
    /// than each order's <c>Year</c>; that's also why legacy <c>Year = 0</c> rows reprice correctly.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct)
    {
        var catalogYear = await GetCurrentEventYearAsync(ct);
        var prices = new Dictionary<Guid, BalanceCalculator.ProductPrice>();
        foreach (var product in await repo.GetAllProductsForYearAsync(catalogYear, ct))
            prices[product.Id] = new BalanceCalculator.ProductPrice(
                product.UnitPriceEur, product.VatRatePercent, product.DepositAmountEur);
        return prices;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> LoadProductNamesAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        (await repo.GetProductsByIdsAsync(productIds, ct)).ToDictionary(p => p.Id, p => p.Name);

    public async Task<OrderDto> MapOrderAsync(
        Order o,
        IReadOnlyDictionary<Guid, string> productNames,
        IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice> currentPrices,
        CancellationToken ct,
        string? displayName = null)
    {
        var balance = BalanceCalculator.Compute(o, currentPrices);
        var totalsByLine = balance.Lines.ToDictionary(t => t.LineId);
        var lines = o.Lines.Select(l =>
        {
            var t = totalsByLine[l.Id];
            return new OrderLineDto(
                l.Id, l.OrderId, l.ProductId,
                productNames.GetValueOrDefault(l.ProductId, Resources.GetString("Store_UnknownProduct", CultureInfo.CurrentUICulture)!),
                l.Qty, t.EffectiveUnitPrice, t.EffectiveVatRate, t.EffectiveDeposit, l.AddedAt,
                t.SubtotalEur, t.VatEur, t.DepositEur, t.TotalEur);
        }).ToList();

        var payments = o.Payments
            .Select(p => new OrderPaymentDto(
                p.Id, p.AmountEur, p.Method, p.Status, p.StripePaymentIntentId, p.ExternalRef, p.ReceivedAt, p.Notes))
            .ToList();

        var counterpartyType = o.TeamId is not null
            ? OrderCounterpartyType.Team
            : OrderCounterpartyType.Camp;

        displayName ??= await ResolveCounterpartyDisplayNameAsync(o, ct);

        return new OrderDto(
            o.Id,
            o.CampSeasonId,
            o.TeamId,
            counterpartyType,
            displayName,
            o.Year,
            o.State,
            o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
            o.IssuedInvoiceId,
            lines,
            payments,
            balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
            balance.PaymentsTotalEur, balance.BalanceEur,
            o.CreatedAt);
    }

    public async Task<string> ResolveCounterpartyDisplayNameAsync(Order o, CancellationToken ct)
    {
        if (o.TeamId is { } tid)
        {
            var team = await teamService.GetTeamAsync(tid, ct);
            return team?.Name ?? Resources.GetString("Store_UnknownTeam", CultureInfo.CurrentUICulture)!;
        }
        if (o.CampSeasonId is { } sid)
        {
            var season = await campService.GetCampSeasonByIdAsync(sid, ct);
            return season?.Name ?? Resources.GetString("Store_UnknownCamp", CultureInfo.CurrentUICulture)!;
        }
        return Resources.GetString("Store_UnknownCounterparty", CultureInfo.CurrentUICulture)!;
    }
}
