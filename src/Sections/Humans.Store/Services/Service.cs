using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Holded.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Teams.Contracts;
using Humans.Store.Services.Dtos;
using Humans.Stripe.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Store.Services;

internal sealed class Service(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampServiceRead campService,
    ITeamServiceRead teamService,
    IClock clock,
    ISettingsService settingsService,
    IStripeService stripeService,
    IHoldedClient holdedClient,
    IOptions<StoreSectionOptions> options,
    ILogger<Service> logger) : IStoreAccountingRead
{
    private readonly StoreOrderReader orderReader = new(repo, campService, teamService, clock, settingsService);

    public Task<IndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default) =>
        BuildIndexDataAsync(userId, allCounterparties: false, ct);

    public Task<IndexData> GetAllCounterpartiesIndexDataAsync(CancellationToken ct = default) =>
        BuildIndexDataAsync(userId: Guid.Empty, allCounterparties: true, ct);

    private async Task<IndexData> BuildIndexDataAsync(
        Guid userId,
        bool allCounterparties,
        CancellationToken ct)
    {
        var year = await GetCurrentEventYearAsync();
        var catalog = (await GetActiveCatalogAsync(year, ct))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var counterparties = new List<CounterpartyOrders>();

        // Camp counterparties: the one camp the viewer leads, or every camp's
        // season for the year when the viewer is a privileged reader.
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

        // Team counterparties — top-level departments only. The viewer's own
        // coordinated departments, or every department when a privileged reader.
        // Order is the controller / view's concern (memory/architecture/display-sort-in-controllers.md).
        var teams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.ParentTeamId is null
                        && (allCounterparties
                            || (t.ManagementRoleHolderUserIds is not null
                                && t.ManagementRoleHolderUserIds.Contains(userId))))
            .ToList();
        var campOrders = await repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
            campSeasons.Select(s => s.Id).ToList(), ct);
        var teamOrders = await repo.GetOrdersForTeamsWithLinesAsync(
            teams.Select(t => t.Id).ToList(), year, ct);
        var productIds = campOrders.Concat(teamOrders)
            .SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        var campOrdersBySeason = campOrders.ToLookup(o => o.CampSeasonId);
        var teamOrdersByTeam = teamOrders.ToLookup(o => o.TeamId);

        foreach (var season in campSeasons)
        {
            // One order per camp-season; if legacy data has multiple, surface
            // only the highest-balance one and let the admin delete the rest.
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

    public async Task<OrderPageData> GetOrderPageDataAsync(
        OrderDto order,
        bool canEdit,
        bool canPayAuthorized,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProductDto> catalog = [];
        if (canEdit)
        {
            catalog = (await GetActiveCatalogAsync(order.Year, ct))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        // A pending async payment (e.g. SEPA mandate captured, not yet cleared) is excluded from
        // BalanceEur, so without this guard the full balance would stay payable a second time
        // while the mandate settles — a double-charge window.
        var hasPendingPayment = order.Payments.Any(p => p.Status == PaymentStatus.Pending);

        return new OrderPageData(
            order,
            catalog,
            order.CounterpartyDisplayName,
            canEdit,
            canPayAuthorized && order.BalanceEur > 0 && !hasPendingPayment && order.CounterpartyType == OrderCounterpartyType.Camp,
            stripeService.IsStoreCheckoutConfigured);
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

    public async Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var now = clock.GetCurrentInstant();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Year = draft.Year,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            UnitPriceEur = draft.UnitPriceEur,
            VatRatePercent = draft.VatRatePercent,
            DepositAmountEur = draft.DepositAmountEur,
            HoldedRevenueAccountNum = draft.HoldedRevenueAccountNum,
            OrderableUntil = draft.OrderableUntil,
            IsActive = draft.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductCreated, AuditEntityTypes.Product, product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return product.Id;
    }

    public async Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateProductDraft(draft);

        var product = await repo.GetProductByIdAsync(draft.Id, ct)
            ?? throw new StoreRuleException($"Product {draft.Id} not found");

        var oldPrice = product.UnitPriceEur;

        product.Year = draft.Year;
        product.Name = draft.Name.Trim();
        product.Description = draft.Description;
        product.UnitPriceEur = draft.UnitPriceEur;
        product.VatRatePercent = draft.VatRatePercent;
        product.DepositAmountEur = draft.DepositAmountEur;
        product.HoldedRevenueAccountNum = draft.HoldedRevenueAccountNum;
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductUpdated, AuditEntityTypes.Product, product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);

        // Dedicated, queryable price-change event for the order-page audit view (#816).
        if (oldPrice != draft.UnitPriceEur)
            await audit.LogAsync(
                AuditAction.StoreProductPriceChanged, AuditEntityTypes.Product, product.Id,
                $"Price for {product.Name} changed from {oldPrice:0.00} to {draft.UnitPriceEur:0.00}",
                actorUserId);
    }

    public async Task<CatalogSaveResult> SaveProductWithResultAsync(
        ProductSaveRequest request,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var parseResult = LocalDatePattern.Iso.Parse(request.OrderableUntil ?? string.Empty);
        if (!parseResult.Success)
            return CatalogSaveResult.Failure(nameof(request.OrderableUntil), "Invalid date - use YYYY-MM-DD.");

        var dto = new ProductDto(
            request.Id ?? Guid.Empty,
            request.Year,
            request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.UnitPriceEur,
            request.VatRatePercent,
            request.DepositAmountEur,
            parseResult.Value,
            request.IsActive,
            request.HoldedRevenueAccountNum);

        try
        {
            if (request.Id is null)
            {
                await CreateProductAsync(dto, actorUserId, ct);
                return CatalogSaveResult.Success(created: true);
            }

            await UpdateProductAsync(dto, actorUserId, ct);
            return CatalogSaveResult.Success(created: false);
        }
        catch (StoreValidationException ex)
        {
            logger.LogWarning("Store catalog Save validation failed: {Reason}", ex.Message);
            return CatalogSaveResult.Failure(null, ex.Message);
        }
        catch (StoreRuleException ex)
        {
            logger.LogWarning("Store catalog Save rejected: {Reason}", ex.Message);
            return CatalogSaveResult.Failure(null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to save Store product {ProductId}", request.Id);
            return new CatalogSaveResult(false, false, null, null);
        }
    }

    public async Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
    {
        var product = await repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        product.IsActive = false;
        product.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateProductAsync(product, ct);

        await audit.LogAsync(
            AuditAction.StoreProductDeactivated, AuditEntityTypes.Product, productId,
            $"Deactivated store product '{product.Name}'",
            actorUserId);
    }

    private static void ValidateProductDraft(ProductDto draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new StoreValidationException("Product name is required", nameof(draft));
        if (draft.UnitPriceEur < 0m)
            throw new StoreValidationException("Unit price cannot be negative", nameof(draft));
        if (draft.VatRatePercent < 0m)
            throw new StoreValidationException("VAT rate cannot be negative", nameof(draft));
        if (draft.DepositAmountEur is < 0m)
            throw new StoreValidationException("Deposit cannot be negative", nameof(draft));
        if (draft.HoldedRevenueAccountNum is { } account and (< 10_000_000 or > 99_999_999))
            throw new StoreValidationException("Holded revenue account must be an 8-digit chart number", nameof(draft));
    }

    public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        var productIds = orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        var result = new List<OrderDto>(orders.Count);
        foreach (var o in orders)
            result.Add(await MapOrderAsync(o, productNames, currentPrices, ct));
        return result;
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        return await MapOrderAsync(o, productNames, currentPrices, ct);
    }

    public async Task<OrderYearRepairReport> GetOrderYearRepairReportAsync(CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersWithMissingYearAsync(ct);
        var rows = new List<OrderYearRepairRow>(orders.Count);
        foreach (var order in orders)
        {
            var season = order.CampSeasonId is { } seasonId
                ? await campService.GetCampSeasonByIdAsync(seasonId, ct)
                : null;
            rows.Add(new OrderYearRepairRow(
                order.Id,
                order.CampSeasonId,
                season?.Name,
                season?.Year));
        }

        return new OrderYearRepairReport(rows);
    }

    public async Task<int> RepairOrderYearsAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersWithMissingYearAsync(ct);
        var repaired = 0;
        foreach (var order in orders)
        {
            if (await ResolveLegacyOrderYearAsync(order, actorUserId, nameof(RepairOrderYearsAsync), ct))
                repaired++;
        }

        return repaired;
    }

    public async Task<IReadOnlyList<PaymentMethodRepairRow>> GetPaymentMethodRepairRowsAsync(CancellationToken ct = default)
    {
        var payments = await repo.GetPaymentsMissingMethodNameAsync(ct);
        return payments
            .Select(p => new PaymentMethodRepairRow(p.Id, p.OrderId, p.Method, p.AmountEur, p.ReceivedAt))
            .ToList();
    }

    /// <summary>
    /// Copies each legacy payment's int <see cref="Payment.Method"/> into its string
    /// <see cref="Payment.MethodName"/>, one audit entry per row. Rescans at run time.
    /// </summary>
    public async Task<int> RepairPaymentMethodNamesAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var payments = await repo.GetPaymentsMissingMethodNameAsync(ct);
        foreach (var payment in payments)
        {
            await repo.SetPaymentMethodNameAsync(payment.Id, payment.Method, ct);
            await audit.LogAsync(
                AuditAction.StorePaymentMethodBackfilled, AuditEntityTypes.Payment, payment.Id,
                $"Copied payment method {payment.Method} into the string column",
                actorUserId, payment.OrderId, AuditEntityTypes.Order);
        }

        return payments.Count;
    }

    private async Task<bool> ResolveLegacyOrderYearAsync(
        Order order,
        Guid actorUserId,
        string source,
        CancellationToken ct)
    {
        if (order.Year != 0 || order.CampSeasonId is not { } seasonId)
            return false;

        var season = await campService.GetCampSeasonByIdAsync(seasonId, ct);
        if (season is null)
            return false;

        order.Year = season.Year;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateOrderAsync(order, ct);
        var description = $"Resolved legacy store order year as {season.Year} from camp season {seasonId}";
        await audit.LogAsync(
            AuditAction.StoreOrderYearBackfilled,
            AuditEntityTypes.Order,
            order.Id,
            description,
            actorUserId);
        logger.LogInformation(
            "Resolved legacy Store order {OrderId} year as {Year} from camp season {CampSeasonId} via {Source}",
            order.Id,
            season.Year,
            seasonId,
            source);
        return true;
    }

    public async Task<MutationResult> CreateOrderAsync(Guid campSeasonId, Guid actorUserId, CancellationToken ct = default)
    {
        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, ct);
        if (season is null)
            return RefuseCreation("Store_CampSeasonMissing", "camp season", campSeasonId, actorUserId);

        // No year filter: a CampSeason *is* a (camp, year) pair, so every order returned here
        // already belongs to season.Year — except a legacy row still at Year = 0, which an
        // `o.Year == season.Year` guard would wave through and hand the season a second order.
        var existing = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        if (existing.Count > 0)
            return RefuseCreation("Store_CampOrderExists", "camp season", campSeasonId, actorUserId);

        var now = clock.GetCurrentInstant();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            TeamId = null,
            Year = season.Year,
            State = OrderState.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderCreated, AuditEntityTypes.Order, order.Id,
            $"Created store order for camp season {campSeasonId}",
            actorUserId);
        return new MutationResult(true, null, order.Id);
    }

    public async Task DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        // An issued order is referenced by its store_invoices row under a restrictive foreign key,
        // and a paid one reads zero-balance — without this the delete would reach EF and blow up.
        if (order.State != OrderState.Open)
            throw new InvalidOperationException(
                $"Order {orderId} has been invoiced; an invoiced order cannot be deleted.");

        // Payments cascade on delete, and a paid-in-full or pending-only order reads zero-balance —
        // any payment row, whatever its status, is a money record that must outlive the order.
        if (order.Payments.Count > 0)
            throw new InvalidOperationException(
                $"Order {orderId} has payments recorded; an order with payments cannot be deleted.");

        var currentPrices = await LoadCurrentPricesAsync(ct);
        var balance = BalanceCalculator.Compute(order, currentPrices).BalanceEur;
        if (balance != 0m)
            throw new InvalidOperationException(
                $"Order {orderId} has a non-zero balance (EUR {balance:0.00}); only zero-balance orders may be deleted.");

        await repo.DeleteOrderAsync(orderId, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderDeleted, AuditEntityTypes.Order, orderId,
            $"Deleted store order {orderId}",
            actorUserId);
    }

    public async Task<MutationResult> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await teamService.GetTeamAsync(teamId, ct);
        if (team is null)
            return RefuseCreation("Store_TeamMissing", "team", teamId, actorUserId);
        if (team.ParentTeamId is not null)
            return RefuseCreation("Store_DepartmentOnly", "team", teamId, actorUserId);

        var year = await GetCurrentEventYearAsync();

        var existing = await repo.GetOrderForTeamAsync(teamId, year, ct);
        if (existing is not null)
            return RefuseCreation("Store_TeamOrderExists", "team", teamId, actorUserId);

        var now = clock.GetCurrentInstant();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CampSeasonId = null,
            TeamId = teamId,
            Year = year,
            State = OrderState.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderCreated, AuditEntityTypes.Order, order.Id,
            $"Created store order for team '{team.Name}' ({year})",
            actorUserId);
        return new MutationResult(true, null, order.Id);
    }

    private MutationResult RefuseCreation(string errorKey, string counterpartyType, Guid counterpartyId, Guid actorUserId)
    {
        logger.LogWarning("Store order creation rejected for {CounterpartyType} {CounterpartyId}, actor {ActorUserId}: {ErrorKey}",
            counterpartyType, counterpartyId, actorUserId, errorKey);
        return MutationResult.Failure(errorKey);
    }

    public async Task<MutationResult> AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
    {
        if (qty <= 0)
            return RefuseMutation("Store_QuantityPositive", orderId, actorUserId);

        var order = await repo.GetOrderByIdAsync(orderId, ct);
        if (order is null) return RefuseMutation("Store_OrderMissing", orderId, actorUserId);

        if (order.State != OrderState.Open)
            return RefuseMutation("Store_OrderLinesFrozen", orderId, actorUserId);

        await ResolveLegacyOrderYearAsync(order, actorUserId, nameof(AddLineAsync), ct);
        if (order.Year == 0)
            return RefuseMutation("Store_OrderYearUnresolved", orderId, actorUserId);

        var product = await repo.GetProductByIdAsync(productId, ct);
        if (product is null) return RefuseMutation("Store_ProductMissing", orderId, actorUserId);

        if (!product.IsActive)
            return RefuseMutation("Store_ProductInactive", orderId, actorUserId);

        if (product.Year != order.Year)
            return RefuseMutation("Store_ProductYearMismatch", orderId, actorUserId);

        // OrderableUntil is gated by OrderAuthorizationHandler (Store admins exempt,
        // everyone else denied) — the auth-free service only annotates the audit entry.
        var today = await TodayInEventZoneAsync();
        var deadlinePassed = today > product.OrderableUntil;

        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ProductId = product.Id,
            Qty = qty,
            UnitPriceSnapshot = product.UnitPriceEur,
            VatRateSnapshot = product.VatRatePercent,
            DepositAmountSnapshot = product.DepositAmountEur,
            AddedAt = clock.GetCurrentInstant(),
            AddedByUserId = actorUserId
        };
        await repo.AddLineAsync(line, ct);

        await audit.LogAsync(
            AuditAction.StoreLineAdded, AuditEntityTypes.OrderLine, line.Id,
            $"Added {qty} × '{product.Name}' to order {order.Id}"
                + (deadlinePassed ? $" (past order deadline {product.OrderableUntil})" : string.Empty),
            actorUserId, order.Id, AuditEntityTypes.Order);

        return MutationResult.Success;
    }

    public async Task<MutationResult> RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default)
    {
        var ctx = await repo.GetLineWithOrderAndProductAsync(lineId, ct);
        if (ctx is null) return RefuseMutation("Store_LineMissing", orderId, actorUserId, lineId);

        if (ctx.OrderId != orderId)
            return RefuseMutation("Store_LineWrongOrder", orderId, actorUserId, lineId);

        if (ctx.OrderState != OrderState.Open)
            return RefuseMutation("Store_OrderLinesFrozen", orderId, actorUserId, lineId);

        // OrderableUntil is gated by OrderAuthorizationHandler (Store admins exempt,
        // everyone else denied) — the auth-free service only annotates the audit entry.
        var today = await TodayInEventZoneAsync();
        var deadlinePassed = today > ctx.ProductOrderableUntil;

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            AuditAction.StoreLineRemoved, AuditEntityTypes.OrderLine, lineId,
            $"Removed line {lineId} from order {ctx.OrderId}"
                + (deadlinePassed ? $" (past order deadline {ctx.ProductOrderableUntil})" : string.Empty),
            actorUserId, ctx.OrderId, AuditEntityTypes.Order);
        return MutationResult.Success;
    }

    public async Task<MutationResult> UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderByIdAsync(orderId, ct);
        if (order is null) return RefuseMutation("Store_OrderMissing", orderId, actorUserId);

        if (order.TeamId is not null)
            return RefuseMutation("Store_NonBillableText", orderId, actorUserId);

        order.CounterpartyName = input.Name;
        order.CounterpartyVatId = input.VatId;
        order.CounterpartyAddress = input.Address;
        order.CounterpartyCountryCode = input.CountryCode;
        order.CounterpartyEmail = input.Email;
        order.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreCounterpartyEdited, AuditEntityTypes.Order, orderId,
            $"Updated counterparty on order {orderId}",
            actorUserId);
        return MutationResult.Success;
    }

    private MutationResult RefuseMutation(string errorKey, Guid orderId, Guid actorUserId, Guid? lineId = null)
    {
        logger.LogWarning("Store mutation rejected for order {OrderId}, line {LineId}, actor {ActorUserId}: {ErrorKey}",
            orderId, lineId, actorUserId, errorKey);
        return MutationResult.Failure(errorKey);
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }

    /// <summary>Returns the active event's catalog year, falling back to the current UTC year before it exists.</summary>
    public Task<int> GetCurrentEventYearAsync() => orderReader.GetCurrentEventYearAsync();

    [ExternalWrite]
    public async Task<CheckoutSessionResult> CreateStripeCheckoutSessionAsync(
        OrderDto order,
        decimal amountEur,
        string returnUrl,
        CancellationToken ct = default)
    {
        string? errorKey = null;
        decimal? maximumAmount = null;
        if (order.CounterpartyType == OrderCounterpartyType.Team)
            errorKey = "Store_NonBillableText";
        else if (!stripeService.IsStoreCheckoutConfigured)
            errorKey = "Store_CheckoutUnconfigured";
        else if (amountEur <= 0)
            errorKey = "Store_PaymentPositive";
        else if (amountEur > order.BalanceEur)
        {
            errorKey = "Store_PaymentAboveBalance";
            maximumAmount = order.BalanceEur;
        }
        else if (order.Payments.Any(p => p.Status == PaymentStatus.Pending))
            errorKey = "Store_PaymentPending";

        if (errorKey is not null)
        {
            logger.LogWarning("Store checkout rejected for order {OrderId}: {ErrorKey}", order.Id, errorKey);
            return new CheckoutSessionResult(null, errorKey, maximumAmount);
        }

        var description = $"Nobodies Collective - {order.CounterpartyName ?? "Camp order"}";
        var sessionUrl = await stripeService.CreateCheckoutSessionAsync(
            storeOrderId: order.Id,
            amountEur: amountEur,
            successUrl: returnUrl,
            cancelUrl: returnUrl,
            customerEmail: order.CounterpartyEmail,
            lineItemDescription: description,
            ct: ct);
        return new CheckoutSessionResult(sessionUrl, null);
    }

    public async Task RecordStripePaymentAsync(
        Guid orderId,
        string paymentIntentId,
        decimal amountEur,
        PaymentStatus status = PaymentStatus.Paid,
        CancellationToken ct = default)
    {
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Stripe payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            throw new ArgumentException("PaymentIntent id is required.", nameof(paymentIntentId));

        if (await repo.StripePaymentIntentExistsAsync(paymentIntentId, ct))
            return; // idempotent: duplicate Stripe webhook delivery

        // Defense-in-depth: never record a payment on a team-owned order.
        var order = await repo.GetOrderByIdAsync(orderId, ct);
        if (order is not null && order.TeamId is not null)
            throw new InvalidOperationException("Team orders are non-billable.");

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = amountEur,
            Method = PaymentMethod.Stripe,
            MethodName = PaymentMethod.Stripe,
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await repo.AddPaymentAsync(payment, ct);
        var settlement = status switch
        {
            PaymentStatus.Pending => "Pending Stripe payment (mandate captured, not yet cleared)",
            PaymentStatus.Failed => "Failed Stripe payment (settlement rejected)",
            _ => "Recorded Stripe payment",
        };
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            $"{settlement} of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, AuditEntityTypes.Order);
    }

    /// <summary>
    /// Records a Store-admin ledger entry: a <see cref="PaymentMethod.DepositReturn"/> credits a
    /// returned deposit (full or partial) back to the order; a <see cref="PaymentMethod.Refund"/>
    /// books money sent back out (issued by hand in the Stripe dashboard). The admin enters a
    /// positive amount; a refund is stored negative and must cite its reference. A refund has no cap — a camp may have
    /// overpaid — but deposit returns can never add up to more than the order's deposits.
    /// </summary>
    public async Task RecordAdminPaymentAsync(
        Guid orderId,
        PaymentMethod method,
        decimal amountEur,
        string? externalRef,
        string? notes,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (method is not (PaymentMethod.DepositReturn or PaymentMethod.Refund))
            throw new InvalidOperationException($"Only deposit returns and refunds can be recorded by hand, not {method}.");
        if (amountEur <= 0)
            throw new InvalidOperationException("Amount must be greater than zero.");
        if (method == PaymentMethod.Refund && string.IsNullOrWhiteSpace(externalRef))
            throw new InvalidOperationException("A refund needs a reference (e.g. the Stripe refund id).");

        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct)
            ?? throw new InvalidOperationException("Order not found.");
        if (order.TeamId is not null)
            throw new InvalidOperationException("Team orders are non-billable.");

        if (method == PaymentMethod.DepositReturn)
        {
            var depositTotal = BalanceCalculator.Compute(order, await LoadCurrentPricesAsync(ct)).DepositTotalEur;
            var alreadyReturned = order.Payments
                .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
                .Sum(p => p.AmountEur);
            var remaining = depositTotal - alreadyReturned;
            if (amountEur > remaining)
                throw new InvalidOperationException(
                    $"Deposit return of EUR {amountEur:0.00} exceeds the EUR {remaining:0.00} of deposit still held "
                    + $"(EUR {depositTotal:0.00} deposited, EUR {alreadyReturned:0.00} already returned).");
        }

        var signed = method == PaymentMethod.Refund ? -amountEur : amountEur;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = signed,
            Method = method,
            MethodName = method,
            Status = PaymentStatus.Paid,
            ExternalRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = actorUserId,
        };
        await repo.AddPaymentAsync(payment, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            $"Recorded {method} of EUR {signed:0.00} on order {orderId}"
                + (payment.ExternalRef is null ? string.Empty : $" (ref {payment.ExternalRef})"),
            actorUserId, orderId, AuditEntityTypes.Order);
    }

    /// <summary>
    /// Admin-only hard delete of one payment row of any method or status, for a row recorded in
    /// error (e.g. a mistaken refund — no money moved in Stripe). The row is gone for good, so the
    /// audit entry carries everything needed to reconstruct it. The order balance is computed, so
    /// it follows by itself.
    /// </summary>
    public async Task DeletePaymentAsync(
        Guid orderId, Guid paymentId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct)
            ?? throw new InvalidOperationException("Order not found.");
        var payment = order.Payments.FirstOrDefault(p => p.Id == paymentId)
            ?? throw new InvalidOperationException("Payment not found on this order.");

        await repo.DeletePaymentAsync(paymentId, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentDeleted, AuditEntityTypes.Payment, payment.Id,
            $"Deleted {payment.Method} payment of EUR {payment.AmountEur:0.00} ({payment.Status}) on order {orderId}, "
                + $"received {payment.ReceivedAt}, ref {payment.ExternalRef ?? "none"}, PI {payment.StripePaymentIntentId ?? "none"}, "
                + $"recorded by {payment.RecordedByUserId?.ToString() ?? "none"}, notes {payment.Notes ?? "none"}",
            actorUserId, orderId, AuditEntityTypes.Order);
    }

    public async Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default)
    {
        var sessionsOrNull = await stripeService.ListStoreCheckoutSessionsAsync(ct);
        var stripeQueried = sessionsOrNull is not null;
        var sessions = sessionsOrNull ?? [];
        var recorded = await repo.GetRecordedStripePaymentsAsync(ct);
        var recordedByPi = recorded.ToDictionary(p => p.PaymentIntentId, p => p.Status, StringComparer.Ordinal);

        // Resolve each distinct matched order once (Stripe-side and recorded-side) for its
        // display label and billable check.
        var matchedIds = sessions.Where(s => s.OrderId is not null).Select(s => s.OrderId!.Value)
            .Concat(recorded.Select(p => p.OrderId))
            .Distinct();
        var orders = new Dictionary<Guid, OrderDto>();
        foreach (var id in matchedIds)
        {
            var order = await GetOrderAsync(id, ct);
            if (order is not null) orders[id] = order;
        }

        var rows = new List<StripeReconciliationRow>(sessions.Count);
        foreach (var s in sessions)
        {
            OrderDto? order = s.OrderId is { } oid && orders.TryGetValue(oid, out var o) ? o : null;
            rows.Add(new StripeReconciliationRow(
                s.SessionId, s.PaymentIntentId, s.AmountEur, s.PaymentStatus, s.CreatedAt,
                s.OrderId, order?.CounterpartyDisplayName,
                ClassifyStripeSession(s, order, recordedByPi)));
        }

        // Orphans: recorded Stripe payments whose PI is absent from the Stripe list — but only
        // when Stripe was actually queried. If it couldn't be read, an empty session list does
        // NOT mean those payments are orphans, so skip the check entirely.
        var stripePis = sessions.Where(s => s.PaymentIntentId is not null)
            .Select(s => s.PaymentIntentId!).ToHashSet(StringComparer.Ordinal);
        var orphans = stripeQueried
            ? recorded
                .Where(p => !stripePis.Contains(p.PaymentIntentId))
                .Select(p => new StripeOrphanPayment(
                    p.PaymentIntentId, p.OrderId,
                    orders.TryGetValue(p.OrderId, out var oo) ? oo.CounterpartyDisplayName : null,
                    p.AmountEur, p.ReceivedAt))
                .ToList()
            : new List<StripeOrphanPayment>();

        return new StripeReconciliationReport(
            stripeService.IsStoreWebhookConfigured,
            stripeService.IsStoreCheckoutConfigured,
            stripeQueried,
            rows,
            orphans);
    }

    private static StripeReconciliationStatus ClassifyStripeSession(
        StoreCheckoutSessionData s, OrderDto? order, IReadOnlyDictionary<string, PaymentStatus> recordedByPi)
    {
        if (!string.Equals(s.PaymentStatus, "paid", StringComparison.Ordinal))
            return StripeReconciliationStatus.Unpaid;
        if (s.PaymentIntentId is { } pi && recordedByPi.TryGetValue(pi, out var paymentStatus))
            // Stripe says paid but the local row hasn't settled — the amount is not in the
            // balance yet, so it must not present as plain "Recorded".
            return paymentStatus switch
            {
                PaymentStatus.Paid => StripeReconciliationStatus.Recorded,
                PaymentStatus.Pending => StripeReconciliationStatus.RecordedPending,
                _ => StripeReconciliationStatus.RecordedFailed,
            };
        if (order is null || s.PaymentIntentId is null || order.CounterpartyType == OrderCounterpartyType.Team)
            return StripeReconciliationStatus.Unmatched;
        return StripeReconciliationStatus.Missing;
    }

    public async Task<StripeReconciliationResult> RecordMissingStripePaymentsAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        var sessions = await stripeService.ListStoreCheckoutSessionsAsync(ct) ?? [];
        var recorded = await repo.GetRecordedStripePaymentsAsync(ct);
        var recordedPis = recorded.Select(p => p.PaymentIntentId).ToHashSet(StringComparer.Ordinal);

        var count = 0;
        var total = 0m;
        foreach (var s in sessions)
        {
            if (!string.Equals(s.PaymentStatus, "paid", StringComparison.Ordinal)) continue;
            if (s.OrderId is not { } orderId || s.PaymentIntentId is not { } pi || s.AmountEur is not { } amount) continue;
            if (amount <= 0 || recordedPis.Contains(pi)) continue;

            // Never fabricate a payment on an unmatched or non-billable (Team) order.
            var order = await repo.GetOrderByIdAsync(orderId, ct);
            if (order is null || order.TeamId is not null) continue;

            await RecordStripePaymentAsync(orderId, pi, amount, ct: ct); // settled (Paid); idempotent on PI id
            count++;
            total += amount;
        }

        if (count > 0)
        {
            await audit.LogAsync(
                AuditAction.StorePaymentsReconciled, "Store", Guid.Empty,
                $"Reconciled {count} Stripe payment(s) totalling EUR {total:0.00} from the Store Stripe account",
                actorUserId);
        }

        return new StripeReconciliationResult(count, total);
    }

    public async Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default)
    {
        switch (evt.Kind)
        {
            case StoreCheckoutEventKind.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompletedAsync(evt, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded:
                await TransitionAsyncPaymentAsync(evt, PaymentStatus.Paid, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
                await TransitionAsyncPaymentAsync(evt, PaymentStatus.Failed, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionExpired:
                await HandleCheckoutSessionExpiredAsync(evt, ct);
                break;

            default:
                logger.LogDebug("Ignoring Stripe webhook event {EventId} of unhandled kind {Kind}", evt.EventId, evt.Kind);
                break;
        }
    }

    private async Task HandleCheckoutSessionCompletedAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Session is not { } session)
        {
            logger.LogWarning("checkout.session.completed event {EventId} did not contain a Session payload", evt.EventId);
            return;
        }

        if (session.OrderId is not { } orderId)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no humans_store_order_id metadata; skipping.",
                session.SessionId);
            return;
        }

        if (session.PaymentIntentId is not { } paymentIntentId)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no PaymentIntentId; skipping.",
                session.SessionId);
            return;
        }

        if (session.AmountEur is not { } amountEur || amountEur <= 0)
        {
            logger.LogWarning(
                "Stripe Checkout Session {SessionId} has non-positive AmountTotal; skipping.",
                session.SessionId);
            return;
        }

        // payment_status distinguishes a settled sync payment (card/wallet → "paid") from an
        // async method where only the debit mandate has been captured (SEPA, delayed Bizum →
        // "unpaid"). A mandate is not money: record it Pending so the order balance does NOT count
        // it until Stripe confirms settlement via async_payment_succeeded. See nobodies-collective/Humans#638.
        var status = string.Equals(session.PaymentStatus, "paid", StringComparison.Ordinal)
            ? PaymentStatus.Paid
            : PaymentStatus.Pending;

        try
        {
            await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, status, ct);
            logger.LogInformation(
                "Recorded Stripe payment ({Status}) for order {OrderId} (session {SessionId}, PI {PaymentIntentId}, EUR {Amount})",
                status, orderId, session.SessionId, paymentIntentId, amountEur);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to record Stripe payment for order {OrderId} (session {SessionId})",
                orderId, session.SessionId);
            // A failed recording must not become a successful webhook acknowledgement.
            throw;
        }
    }

    /// <summary>
    /// Transitions the <see cref="PaymentStatus.Pending"/> payment behind an async Checkout
    /// event to its settled state — <see cref="PaymentStatus.Paid"/> on
    /// <c>async_payment_succeeded</c>, <see cref="PaymentStatus.Failed"/> on
    /// <c>async_payment_failed</c>. Idempotent: a re-delivered event that finds the row already in
    /// the target state is a no-op. Out-of-order tolerance: if a terminal event arrives before
    /// <c>completed</c> (no row yet), the payment is recorded directly in its terminal state so
    /// a later completed event cannot lose settlement or recreate a failed payment as Pending.
    /// </summary>
    private async Task TransitionAsyncPaymentAsync(StoreCheckoutWebhookEvent evt, PaymentStatus target, CancellationToken ct)
    {
        if (evt.Session is not { } session)
        {
            logger.LogWarning("{Kind} event {EventId} did not contain a Session payload", evt.Kind, evt.EventId);
            return;
        }

        if (session.PaymentIntentId is not { } paymentIntentId)
        {
            logger.LogWarning("{Kind} session {SessionId} has no PaymentIntentId; skipping.", evt.Kind, session.SessionId);
            return;
        }

        var existing = await repo.GetPaymentByStripePaymentIntentIdAsync(paymentIntentId, ct);
        if (existing is null)
        {
            // Preserve either terminal outcome before completed; its later delivery no-ops on
            // the unique PI instead of recreating a failed payment as Pending.
            if (session.OrderId is { } orderId
                && session.AmountEur is { } amountEur && amountEur > 0)
            {
                await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, target, ct);
                logger.LogInformation(
                    "Async {Kind} arrived before completed for order {OrderId} (PI {PaymentIntentId}); recorded {Status} payment directly.",
                    evt.Kind, orderId, paymentIntentId, target);
            }
            else
            {
                logger.LogWarning(
                    "{Kind} for PI {PaymentIntentId} found no matching payment; nothing to transition.",
                    evt.Kind, paymentIntentId);
            }
            return;
        }

        if (existing.Status == target)
            return; // idempotent: re-delivery of an already-applied transition

        if (existing.Status != PaymentStatus.Pending)
        {
            // A non-Pending, non-target state (e.g. a Failed row receiving a late success, or vice
            // versa) is anomalous; log and leave the recorded state untouched rather than guess.
            logger.LogWarning(
                "{Kind} for PI {PaymentIntentId} found payment in unexpected state {Status}; leaving unchanged.",
                evt.Kind, paymentIntentId, existing.Status);
            return;
        }

        await repo.UpdatePaymentStatusAsync(existing.Id, target, ct);
        var action = target == PaymentStatus.Paid ? AuditAction.StorePaymentSettled : AuditAction.StorePaymentFailed;
        var verb = target == PaymentStatus.Paid ? "settled" : "failed";
        await audit.LogAsync(
            action, AuditEntityTypes.Payment, existing.Id,
            $"Stripe payment of EUR {existing.AmountEur:0.00} {verb} on order {existing.OrderId} (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, AuditEntityTypes.Order);
    }

    /// <summary>
    /// Handles <c>checkout.session.expired</c>: defensively removes an orphan
    /// <see cref="PaymentStatus.Pending"/> row that somehow predates a missing <c>completed</c>
    /// event. In practice unreachable (a Pending row is created by <c>completed</c>, and an expired
    /// session never reached <c>completed</c>), so this only cleans up edge-case retries. A settled
    /// (Paid) or Failed payment is never touched.
    /// </summary>
    private async Task HandleCheckoutSessionExpiredAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Session is not { PaymentIntentId: { } paymentIntentId })
        {
            // No PI on an expired session is the normal case (payment never started); nothing to do.
            logger.LogDebug("checkout.session.expired event {EventId} has no PaymentIntentId; nothing to clean up.", evt.EventId);
            return;
        }

        var existing = await repo.GetPaymentByStripePaymentIntentIdAsync(paymentIntentId, ct);
        if (existing is null || existing.Status != PaymentStatus.Pending)
            return;

        await repo.DeletePaymentAsync(existing.Id, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentExpired, AuditEntityTypes.Payment, existing.Id,
            $"Removed orphan pending Stripe payment of EUR {existing.AmountEur:0.00} on order {existing.OrderId} after session expiry (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, AuditEntityTypes.Order);
    }

    /// <summary>Issues or recovers the billable order's invoice and freezes its prices.</summary>
    [ExternalWrite]
    public async Task<AdminMutationResult> IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null)
            return RefuseAdminMutation(orderId, actorUserId, "Order not found.");
        if (order.TeamId is not null)
            return RefuseAdminMutation(orderId, actorUserId, "Team orders are non-billable.");
        return await new StoreInvoiceIssuer(repo, audit, clock, holdedClient, options, orderReader, logger)
            .IssueAsync(order, actorUserId, ct);
    }

    private AdminMutationResult RefuseAdminMutation(Guid orderId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store admin mutation rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            orderId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    /// <summary>
    /// What every year-wide view needs before it can price an order: the year's camp seasons
    /// and department teams (for labels and, on the summary, for selecting orders), the
    /// year's catalog, and the live prices Open orders reprice to. Shared by the admin summary
    /// and the accounting export so their totals cannot drift apart.
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

        // Departments — every top-level team on the platform (no user filter here: admin
        // views reflect all departments). Filter to ParentTeamId is null.
        var allTeams = await teamService.GetTeamsAsync(ct);
        var departmentIds = allTeams.Values
            .Where(t => t.ParentTeamId is null)
            .Select(t => t.Id)
            .ToList();
        var teamNames = allTeams.Values.ToDictionary(t => t.Id, t => t.Name);

        // Reprice Open orders to the live catalog, exactly like the order page
        // (MapOrderAsync) — summing raw snapshots here made summary totals drift
        // from order totals whenever a catalog price changed after lines were added.
        // Priced from the requested year's products (already loaded above), not the
        // active event's catalog — historical summaries must not reprice against a
        // later year's prices.
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
        var teamOrders = await repo.GetOrdersForTeamsWithLinesAsync(departmentIds, year, ct);
        var totalsByOrder = PriceAll(campOrdersInYear.Concat(teamOrders), context);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name);

        var byCounterparty = new List<OrderSummaryDto>();

        foreach (var o in campOrdersInYear)
        {
            var totals = totalsByOrder[o.Id];
            var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
            var sid = o.CampSeasonId!.Value;
            var campName = seasonsForYear[sid].Name;
            byCounterparty.Add(new OrderSummaryDto(
                o.Id,
                OrderCounterpartyType.Camp,
                sid,
                campName,
                o.State,
                totalDue,
                totals.PaymentsTotalEur,
                totals.BalanceEur));
        }
        foreach (var o in teamOrders)
        {
            var totals = totalsByOrder[o.Id];
            var tid = o.TeamId!.Value;
            var teamName = teamNames.TryGetValue(tid, out var n) ? n : "(unknown team)";
            byCounterparty.Add(new OrderSummaryDto(
                o.Id,
                OrderCounterpartyType.Team,
                tid,
                teamName,
                o.State,
                totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur,
                0m, // team orders never have payments
                0m));
        }

        // Counterparty row ordering is the view's concern
        // (memory/architecture/display-sort-in-controllers.md).
        // by-item aggregates lines from BOTH camp and team orders so suppliers see the full demand.
        var allLineProjections = campOrdersInYear
            .Concat(teamOrders)
            .SelectMany(o =>
            {
                var lineTotals = totalsByOrder[o.Id].Lines.ToDictionary(t => t.LineId);
                return o.Lines.Select(l => new { l.ProductId, l.Qty, lineTotals[l.Id].TotalEur });
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

        var counterpartyRows = new List<CrossTabRow>();
        foreach (var g in campOrdersInYear.GroupBy(o => o.CampSeasonId!.Value))
        {
            var perProduct = g
                .SelectMany(o => o.Lines)
                .GroupBy(l => l.ProductId)
                .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
            var total = perProduct.Values.Sum();
            counterpartyRows.Add(new CrossTabRow(
                OrderCounterpartyType.Camp,
                g.Key,
                seasonsForYear[g.Key].Name,
                total,
                perProduct));
        }
        foreach (var g in teamOrders.GroupBy(o => o.TeamId!.Value))
        {
            var perProduct = g
                .SelectMany(o => o.Lines)
                .GroupBy(l => l.ProductId)
                .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
            var total = perProduct.Values.Sum();
            counterpartyRows.Add(new CrossTabRow(
                OrderCounterpartyType.Team,
                g.Key,
                teamNames.TryGetValue(g.Key, out var n) ? n : "(unknown team)",
                total,
                perProduct));
        }
        return new SummaryDto(
            year,
            byCounterparty,
            byItem,
            new CrossTabDto(productColumns, counterpartyRows));
    }

    // ── IStoreAccountingRead ────────────────────────────────────────────────

    /// <summary>
    /// The year's orders selected by their persisted <see cref="Order.Year"/>, not through the
    /// counterparty: a camp deleted since (its seasons cascade; Store keeps the bare id) or a
    /// department reparented since still has its money in the books, so the export keeps the
    /// row and labels it as best it can.
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

        // Names and revenue accounts by id, not by year: a line may point at a product that
        // was deactivated or re-homed since it was added, and the export still needs its name.
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
        // Team orders are non-billable and never carry payments; the Paid filter below is
        // what selects, so they contribute nothing rather than being excluded by kind.
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

    private static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum);

    /// <summary>Display names for the given product ids, live or deactivated, any year.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadProductNamesAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        (await repo.GetProductsByIdsAsync(productIds, ct)).ToDictionary(p => p.Id, p => p.Name);

    private Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct) => orderReader.LoadCurrentPricesAsync(ct);

    private async Task<OrderDto> MapOrderAsync(
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
                productNames.GetValueOrDefault(l.ProductId, "(unknown product)"),
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

    private Task<string> ResolveCounterpartyDisplayNameAsync(Order order, CancellationToken ct) =>
        orderReader.ResolveCounterpartyDisplayNameAsync(order, ct);

    private sealed class StoreRuleException : InvalidOperationException
    {
        public StoreRuleException() { }
        public StoreRuleException(string message) : base(message) { }
        public StoreRuleException(string message, Exception innerException) : base(message, innerException) { }
    }

    private sealed class StoreValidationException : ArgumentException
    {
        public StoreValidationException() { }
        public StoreValidationException(string message) : base(message) { }
        public StoreValidationException(string message, Exception innerException) : base(message, innerException) { }
        public StoreValidationException(string message, string paramName) : base(message, paramName) { }
        public StoreValidationException(string message, string paramName, Exception innerException)
            : base(message, paramName, innerException) { }
    }
}
