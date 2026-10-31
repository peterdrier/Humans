using System.Globalization;
using System.Resources;
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
    ISettingsService settingsService) : IStoreAccountingRead
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

    /// <summary>
    /// What every year-wide view needs to price an order. Shared by the admin summary and the accounting export so their
    /// totals can't drift apart.
    /// </summary>
    private sealed record YearContext(
        IReadOnlyDictionary<Guid, CampSeasonInfo> SeasonsForYear,
        IReadOnlyList<Product> Products,
        IReadOnlyList<Guid> DepartmentIds,
        IReadOnlyDictionary<Guid, string> TeamNames,
        IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice> CurrentPrices);

    private async Task<YearContext> LoadYearContextAsync(int year, CancellationToken ct)
    {
        var seasonsForYear = (await campService.GetCampsForYearAsync(year, ct))
            .SelectMany(camp => camp.Seasons.Where(season => season.Year == year))
            .ToDictionary(season => season.Id);
        var products = await repo.GetAllProductsForYearAsync(year, ct);

        var allTeams = await teamService.GetTeamsAsync(ct);
        var departmentIds = allTeams.Values
            .Where(t => t.ParentTeamId is null)
            .Select(t => t.Id)
            .ToList();
        var teamNames = allTeams.Values.ToDictionary(t => t.Id, t => t.Name);

        // Reprice Open orders like the order page does, but from the requested year's products: a historical summary
        // must not reprice against a later year's prices.
        var currentPrices = products.ToDictionary(
            p => p.Id,
            p => new BalanceCalculator.ProductPrice(p.UnitPriceEur, p.VatRatePercent, p.DepositAmountEur));

        return new YearContext(seasonsForYear, products, departmentIds, teamNames, currentPrices);
    }

    private static Dictionary<Guid, BalanceCalculator.Result> PriceAll(
        IEnumerable<Order> orders, YearContext context) =>
        orders.ToDictionary(o => o.Id, o => BalanceCalculator.Compute(o, context.CurrentPrices));

    public async Task<SummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
    {
        var context = await LoadYearContextAsync(year, ct);
        var (seasonsForYear, products, departmentIds, teamNames, _) = context;

        var campOrders = seasonsForYear.Count == 0
            ? Array.Empty<Order>()
            : await repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                seasonsForYear.Keys.ToList(), ct);

        var campOrdersInYear = campOrders
            .Where(o => o.CampSeasonId is { } sid && seasonsForYear.ContainsKey(sid))
            .ToList();
        var teamOrders = await repo.GetOrdersForTeamsWithLinesAndPaymentsAsync(departmentIds, year, ct);
        var counterparties = campOrdersInYear
            .Select(o => (Order: o, Type: OrderCounterpartyType.Camp,
                Id: o.CampSeasonId!.Value, Name: seasonsForYear[o.CampSeasonId.Value].Name))
            .Concat(teamOrders.Select(o => (Order: o, Type: OrderCounterpartyType.Team,
                Id: o.TeamId!.Value, Name: teamNames.GetValueOrDefault(o.TeamId.Value, "(unknown team)"))))
            .ToList();
        var totalsByOrder = PriceAll(counterparties.Select(c => c.Order), context);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name);

        var byCounterparty = counterparties.Select(c =>
        {
            var totals = totalsByOrder[c.Order.Id];
            var billable = c.Type == OrderCounterpartyType.Camp;
            return new OrderSummaryDto(
                c.Order.Id, c.Type, c.Id, c.Name, c.Order.State,
                totals.TotalDueEur,
                billable ? totals.PaymentsTotalEur : 0m,
                billable ? totals.BalanceEur : 0m);
        }).ToList();

        // By-item aggregates camp AND team orders so suppliers see the full demand.
        var allLineProjections = counterparties
            .SelectMany(c =>
            {
                var lineTotals = totalsByOrder[c.Order.Id].Lines.ToDictionary(t => t.LineId);
                return c.Order.Lines.Select(l => new { l.ProductId, l.Qty, lineTotals[l.Id].TotalEur });
            })
            .ToList();

        var byItem = allLineProjections
            .GroupBy(x => x.ProductId)
            .Select(g => new ProductAggregateDto(
                g.Key,
                productNames.TryGetValue(g.Key, out var n) ? n : "(unknown)",
                g.Sum(x => x.Qty),
                g.Sum(x => x.TotalEur)))
            .OrderByDescending(p => p.TotalQty)
            .ThenBy(p => p.ProductName, StringComparer.Ordinal)
            .ToList();

        var productColumns = byItem
            .Select(p => new CrossTabColumn(p.ProductId, p.ProductName, p.TotalQty))
            .OrderBy(c => c.ProductName, StringComparer.Ordinal)
            .ToList();

        var counterpartyRows = counterparties
            .GroupBy(c => (c.Type, c.Id))
            .Select(g =>
            {
                var perProduct = g
                    .SelectMany(c => c.Order.Lines)
                    .GroupBy(l => l.ProductId)
                    .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
                return new CrossTabRow(
                    g.Key.Type, g.Key.Id, g.First().Name, perProduct.Values.Sum(), perProduct);
            }).ToList();
        return new SummaryDto(
            year,
            byCounterparty,
            byItem,
            new CrossTabDto(productColumns, counterpartyRows));
    }

    /// <summary>
    /// Selected by persisted <see cref="Order.Year"/>, not through the counterparty: a since-deleted camp or reparented
    /// department still has its money in the books.
    /// </summary>
    private async Task<(IReadOnlyList<Order> Orders, YearContext Context)> LoadYearOrdersForAccountingAsync(
        int year, CancellationToken ct)
    {
        var context = await LoadYearContextAsync(year, ct);
        var orders = await repo.GetOrdersForYearWithLinesAndPaymentsAsync(
            year, context.SeasonsForYear.Keys.ToList(), ct);
        return (orders, context);
    }

    public async Task<IReadOnlyList<AccountingOrderLineDto>> GetOrderLinesAsync(int year, CancellationToken ct = default)
    {
        var (orders, context) = await LoadYearOrdersForAccountingAsync(year, ct);
        var totalsByOrder = PriceAll(orders, context);

        // By id, not year: a line may point at a product deactivated or re-homed since, and the export still needs its name.
        var products = (await repo.GetProductsByIdsAsync(
                orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);
        var invoiceNumbers = (await repo.GetInvoicesForOrdersAsync(
                orders.Where(o => o.IssuedInvoiceId is not null).Select(o => o.Id).ToList(), ct))
            .ToDictionary(i => i.OrderId, i => i.HoldedDocNumber);

        var rows = new List<AccountingOrderLineDto>();
        foreach (var o in orders)
        {
            var (counterpartyType, label) = CounterpartyOf(o, context);
            var totalsByLine = totalsByOrder[o.Id].Lines.ToDictionary(t => t.LineId);
            foreach (var l in o.Lines)
            {
                var t = totalsByLine[l.Id];
                products.TryGetValue(l.ProductId, out var product);
                rows.Add(new AccountingOrderLineDto(
                    year,
                    o.Id,
                    counterpartyType,
                    label,
                    o.CounterpartyName,
                    o.CounterpartyVatId,
                    o.CounterpartyCountryCode,
                    o.State,
                    invoiceNumbers.GetValueOrDefault(o.Id),
                    l.Id,
                    l.ProductId,
                    product?.Name ?? "(unknown product)",
                    product?.HoldedRevenueAccountNum,
                    l.Qty,
                    t.EffectiveUnitPrice,
                    t.EffectiveVatRate,
                    t.TotalEur,
                    t.SubtotalEur,
                    t.VatEur,
                    t.DepositEur,
                    l.AddedAt));
            }
        }
        return rows;
    }

    public async Task<IReadOnlyList<AccountingPaymentDto>> GetPaymentsAsync(int year, CancellationToken ct = default)
    {
        // Team orders never carry payments; the Paid filter below is what selects.
        var (orders, context) = await LoadYearOrdersForAccountingAsync(year, ct);

        var rows = new List<AccountingPaymentDto>();
        foreach (var o in orders)
        {
            var (counterpartyType, label) = CounterpartyOf(o, context);
            foreach (var p in o.Payments.Where(p => p.Status == PaymentStatus.Paid))
            {
                rows.Add(new AccountingPaymentDto(
                    year,
                    o.Id,
                    counterpartyType,
                    label,
                    p.Id,
                    p.AmountEur,
                    p.Method.ToString(),
                    p.StripePaymentIntentId,
                    p.ExternalRef,
                    p.ReceivedAt));
            }
        }
        return rows;
    }

    private static (OrderCounterpartyType Type, string Label) CounterpartyOf(Order o, YearContext context) =>
        o.TeamId is { } tid
            ? (OrderCounterpartyType.Team, context.TeamNames.GetValueOrDefault(tid, "(unknown team)"))
            : (OrderCounterpartyType.Camp,
                o.CampSeasonId is { } sid && context.SeasonsForYear.TryGetValue(sid, out var season)
                    ? season.Name
                    : "(unknown camp)");

}
