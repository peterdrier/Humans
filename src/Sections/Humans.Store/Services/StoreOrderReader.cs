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

    public Task<IndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default) =>
        BuildIndexDataAsync(userId, allCounterparties: false, ct);

    public Task<IndexData> GetAllCounterpartiesIndexDataAsync(CancellationToken ct = default) =>
        BuildIndexDataAsync(userId: Guid.Empty, allCounterparties: true, ct);

    private async Task<IndexData> BuildIndexDataAsync(
        Guid userId,
        bool allCounterparties,
        CancellationToken ct)
    {
        var year = await GetCurrentEventYearAsync(ct);
        var catalog = await GetActiveCatalogAsync(year, ct);

        var counterparties = new List<CounterpartyOrders>();

        var campSeasons = new List<CampSeasonInfo>();
        foreach (var camp in await campService.GetCampsForYearAsync(year, ct))
        {
            if (allCounterparties)
            {
                var season = camp.GetSeasonForYear(year);
                if (season is not null) campSeasons.Add(season);
            }
            else
            {
                var leadSeasonId = camp.GetLeadSeasonIdForYear(userId, year);
                if (leadSeasonId is null) continue;
                campSeasons.Add(camp.Seasons.First(season => season.Id == leadSeasonId.Value));
                break; // a user leads at most one camp
            }
        }

        var teams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.ParentTeamId is null
                        && (allCounterparties
                            || (t.ManagementRoleHolderUserIds is not null
                                && t.ManagementRoleHolderUserIds.Contains(userId))))
            .ToList();
        var campOrders = await repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
            campSeasons.Select(s => s.Id).ToList(), ct);
        var teamOrders = await repo.GetOrdersForTeamsWithLinesAndPaymentsAsync(
            teams.Select(t => t.Id).ToList(), year, ct);
        var productIds = campOrders.Concat(teamOrders)
            .SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        var campOrdersBySeason = campOrders.ToLookup(o => o.CampSeasonId);
        var teamOrdersByTeam = teamOrders.ToLookup(o => o.TeamId);

        foreach (var season in campSeasons)
        {
            // One order per camp-season; for legacy duplicates, surface the highest-balance one and let the admin
            // delete the rest.
            var allOrders = new List<OrderDto>();
            foreach (var order in campOrdersBySeason[season.Id])
                allOrders.Add(await MapOrderAsync(order, productNames, currentPrices, ct, season.Name));
            var primary = allOrders
                .OrderByDescending(o => o.BalanceEur)
                .FirstOrDefault();
            IReadOnlyList<OrderDto> orders = primary is null ? [] : [primary];
            counterparties.Add(new CounterpartyOrders(
                OrderCounterpartyType.Camp,
                season.Id,
                season.Name,
                year,
                orders));
        }

        foreach (var team in teams)
        {
            var existing = teamOrdersByTeam[team.Id].FirstOrDefault();
            IReadOnlyList<OrderDto> orders = existing is null
                ? []
                : [await MapOrderAsync(existing, productNames, currentPrices, ct, team.Name)];
            counterparties.Add(new CounterpartyOrders(
                OrderCounterpartyType.Team,
                team.Id,
                team.Name,
                year,
                orders));
        }

        return new IndexData(
            year,
            catalog,
            counterparties);
    }

    public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetActiveProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetAllProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var p = await repo.GetProductByIdAsync(productId, ct);
        return p is null ? null : MapProduct(p);
    }

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
    private static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum);

}
