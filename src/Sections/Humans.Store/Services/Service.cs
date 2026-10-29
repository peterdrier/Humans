using System.Globalization;
using System.Resources;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Threading;
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
    ICampService campService,
    ITeamService teamService,
    IClock clock,
    ISettingsService settingsService,
    IStripeService stripeService,
    IHoldedClient holdedClient,
    IOptions<StoreSectionOptions> options,
    ILogger<Service> logger) : IStoreAccountingRead
{
    private static readonly TrackedLock StripePaymentLock = new("Store.StripePayments");
    private static readonly ResourceManager Resources = new(typeof(StoreResource));

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
        var productNames = await orderReader.LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        var campOrdersBySeason = campOrders.ToLookup(o => o.CampSeasonId);
        var teamOrdersByTeam = teamOrders.ToLookup(o => o.TeamId);

        foreach (var season in campSeasons)
        {
            // One order per camp-season; for legacy duplicates, surface the highest-balance one and let the admin
            // delete the rest.
            var allOrders = new List<OrderDto>();
            foreach (var order in campOrdersBySeason[season.Id])
                allOrders.Add(await orderReader.MapOrderAsync(order, productNames, currentPrices, ct, season.Name));
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
                : [await orderReader.MapOrderAsync(existing, productNames, currentPrices, ct, team.Name)];
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
            catalog = await GetActiveCatalogAsync(order.Year, ct);
        }

        // A pending async payment is excluded from BalanceEur, so without this the full balance stays payable a second
        // time while the mandate settles: a double charge.
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

    public async Task<CatalogSaveResult> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        var refusal = GetProductDraftRefusal(draft);
        if (refusal is not null)
        {
            logger.LogWarning("Store catalog create rejected for actor {ActorId}: {Reason}", actorUserId, refusal);
            return CatalogSaveResult.Failure(null, refusal);
        }

        var now = clock.GetCurrentInstant();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyProductFields(product, draft);
        await repo.AddProductAsync(product, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreProductCreated, AuditEntityTypes.Product, product.Id,
            $"Created store product '{product.Name}' for year {product.Year}",
            actorUserId);
        return CatalogSaveResult.Success(created: true);
    }

    public async Task<AdminMutationResult> UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
    {
        var refusal = GetProductDraftRefusal(draft);
        if (refusal is not null)
            return RefuseCatalogMutation(draft.Id, actorUserId, refusal);

        var product = await repo.GetProductByIdAsync(draft.Id, ct);
        if (product is null)
            return RefuseCatalogMutation(draft.Id, actorUserId, $"Product {draft.Id} not found");

        var oldPrice = product.UnitPriceEur;

        ApplyProductFields(product, draft);
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreProductUpdated, AuditEntityTypes.Product, product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);

        // A dedicated, queryable price-change event for the order page's audit view.
        if (oldPrice != draft.UnitPriceEur)
            await audit.LogAsync(
                StoreAuditActions.StoreProductPriceChanged, AuditEntityTypes.Product, product.Id,
                $"Price for {product.Name} changed from {oldPrice:0.00} to {draft.UnitPriceEur:0.00}",
                actorUserId);
        return AdminMutationResult.Success;
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
                return await CreateProductAsync(dto, actorUserId, ct);

            var result = await UpdateProductAsync(dto, actorUserId, ct);
            return result.Succeeded
                ? CatalogSaveResult.Success(created: false)
                : CatalogSaveResult.Failure(null, result.Refusal!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to save Store product {ProductId}", request.Id);
            return new CatalogSaveResult(false, false, null, null);
        }
    }

    public async Task<AdminMutationResult> DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
    {
        var product = await repo.GetProductByIdAsync(productId, ct);
        if (product is null)
            return RefuseCatalogMutation(productId, actorUserId, $"Product {productId} not found");

        product.IsActive = false;
        product.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateProductAsync(product, ct);

        await audit.LogAsync(
            StoreAuditActions.StoreProductDeactivated, AuditEntityTypes.Product, productId,
            $"Deactivated store product '{product.Name}'",
            actorUserId);
        return AdminMutationResult.Success;
    }

    private AdminMutationResult RefuseCatalogMutation(Guid productId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store catalog mutation rejected for product {ProductId}, actor {ActorId}: {Reason}",
            productId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    private static void ApplyProductFields(Product product, ProductDto draft)
    {
        product.Year = draft.Year;
        product.Name = draft.Name.Trim();
        product.Description = draft.Description;
        product.UnitPriceEur = draft.UnitPriceEur;
        product.VatRatePercent = draft.VatRatePercent;
        product.DepositAmountEur = draft.DepositAmountEur;
        product.HoldedRevenueAccountNum = draft.HoldedRevenueAccountNum;
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
    }

    private static string? GetProductDraftRefusal(ProductDto draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            return "Product name is required";
        if (draft.UnitPriceEur < 0m)
            return "Unit price cannot be negative";
        if (draft.VatRatePercent < 0m)
            return "VAT rate cannot be negative";
        if (draft.DepositAmountEur is < 0m)
            return "Deposit cannot be negative";
        if (draft.HoldedRevenueAccountNum is < 10_000_000 or > 99_999_999)
            return "Holded revenue account must be an 8-digit chart number";
        return null;
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await orderReader.LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        return await orderReader.MapOrderAsync(o, productNames, currentPrices, ct);
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

    /// <summary>Copies each legacy payment's int <see cref="Payment.Method"/> into <see cref="Payment.MethodName"/>, one audit entry per row.</summary>
    public async Task<int> RepairPaymentMethodNamesAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var payments = await repo.GetPaymentsMissingMethodNameAsync(ct);
        foreach (var payment in payments)
        {
            await repo.SetPaymentMethodNameAsync(payment.Id, payment.Method, ct);
            await audit.LogAsync(
                StoreAuditActions.StorePaymentMethodBackfilled, AuditEntityTypes.Payment, payment.Id,
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
            StoreAuditActions.StoreOrderYearBackfilled,
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

        // No year filter: a CampSeason already is a (camp, year) pair, and an `o.Year == season.Year` guard would wave
        // through a legacy Year = 0 row and hand the season a second order.
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
            StoreAuditActions.StoreOrderCreated, AuditEntityTypes.Order, order.Id,
            $"Created store order for camp season {campSeasonId}",
            actorUserId);
        return new MutationResult(true, null, order.Id);
    }

    public async Task<AdminMutationResult> DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null) return RefuseAdminMutation(orderId, actorUserId, "Order not found.");

        // An issued order is held by a restrictive FK from store_invoices, and a paid one reads zero-balance; without
        // this the delete blows up in EF.
        if (order.State != OrderState.Open)
            return RefuseAdminMutation(orderId, actorUserId,
                $"Order {orderId} has been invoiced; an invoiced order cannot be deleted.");

        // Payments cascade on delete, and any payment row, whatever its status, is a money record that must outlive
        // the order.
        if (order.Payments.Count > 0)
            return RefuseAdminMutation(orderId, actorUserId,
                $"Order {orderId} has payments recorded; an order with payments cannot be deleted.");

        var currentPrices = await LoadCurrentPricesAsync(ct);
        var balance = BalanceCalculator.Compute(order, currentPrices).BalanceEur;
        if (balance != 0m)
            return RefuseAdminMutation(orderId, actorUserId,
                $"Order {orderId} has a non-zero balance (EUR {balance:0.00}); only zero-balance orders may be deleted.");

        await repo.DeleteOrderAsync(orderId, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreOrderDeleted, AuditEntityTypes.Order, orderId,
            $"Deleted store order {orderId}",
            actorUserId);
        return AdminMutationResult.Success;
    }

    public async Task<MutationResult> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await teamService.GetTeamAsync(teamId, ct);
        if (team is null)
            return RefuseCreation("Store_TeamMissing", "team", teamId, actorUserId);
        if (team.ParentTeamId is not null)
            return RefuseCreation("Store_DepartmentOnly", "team", teamId, actorUserId);

        var year = await GetCurrentEventYearAsync(ct);

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
            StoreAuditActions.StoreOrderCreated, AuditEntityTypes.Order, order.Id,
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

        // OrderableUntil is enforced by OrderAuthorizationHandler; the auth-free service only annotates the audit entry.
        var today = await settingsService.TodayInEventZoneAsync(clock, ct);
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
            StoreAuditActions.StoreLineAdded, AuditEntityTypes.OrderLine, line.Id,
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

        // OrderableUntil is enforced by OrderAuthorizationHandler; the auth-free service only annotates the audit entry.
        var today = await settingsService.TodayInEventZoneAsync(clock, ct);
        var deadlinePassed = today > ctx.ProductOrderableUntil;

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreLineRemoved, AuditEntityTypes.OrderLine, lineId,
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

        if (input.Name is { Length: > 200 } || input.VatId is { Length: > 50 }
            || input.Address is { Length: > 500 } || input.CountryCode is { Length: > 2 }
            || input.Email is { Length: > 320 })
            return RefuseMutation("Store_CounterpartyTooLong", orderId, actorUserId);

        order.CounterpartyName = input.Name;
        order.CounterpartyVatId = input.VatId;
        order.CounterpartyAddress = input.Address;
        order.CounterpartyCountryCode = input.CountryCode;
        order.CounterpartyEmail = input.Email;
        order.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateOrderAsync(order, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreCounterpartyEdited, AuditEntityTypes.Order, orderId,
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

    /// <summary>Falls back to the current UTC year before an active event exists.</summary>
    public Task<int> GetCurrentEventYearAsync(CancellationToken ct = default) => orderReader.GetCurrentEventYearAsync(ct);

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

        var name = order.CounterpartyName ?? Resources.GetString("Store_CheckoutCampOrder", CultureInfo.CurrentUICulture)!;
        var description = $"Nobodies Collective - {name}";
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
        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
        await RecordStripePaymentCoreAsync(orderId, paymentIntentId, amountEur, status, ct);
    }

    // Called only while the ingestion lock is held, including from webhook state transitions.
    private async Task RecordStripePaymentCoreAsync(
        Guid orderId, string paymentIntentId, decimal amountEur, PaymentStatus status, CancellationToken ct)
    {
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Stripe payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            throw new ArgumentException("PaymentIntent id is required.", nameof(paymentIntentId));

        if (await repo.StripePaymentIntentExistsAsync(paymentIntentId, ct))
            return; // duplicate Stripe webhook delivery

        // Defense in depth: never record a payment on a team-owned order.
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
            StoreAuditActions.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            $"{settlement} of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, AuditEntityTypes.Order);
    }

    /// <summary>
    /// A <see cref="PaymentMethod.DepositReturn"/> credits a returned deposit; a <see cref="PaymentMethod.Refund"/>
    /// books money sent back by hand from the Stripe dashboard, stored negative and citing its reference. Refunds are
    /// uncapped (a camp may have overpaid); deposit returns never exceed the order's deposits.
    /// </summary>
    public async Task<AdminMutationResult> RecordAdminPaymentAsync(
        Guid orderId,
        PaymentMethod method,
        decimal amountEur,
        string? externalRef,
        string? notes,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (method is not (PaymentMethod.DepositReturn or PaymentMethod.Refund))
            return RefuseAdminMutation(orderId, actorUserId, $"Only deposit returns and refunds can be recorded by hand, not {method}.");
        if (amountEur <= 0)
            return RefuseAdminMutation(orderId, actorUserId, "Amount must be greater than zero.");
        if (method == PaymentMethod.Refund && string.IsNullOrWhiteSpace(externalRef))
            return RefuseAdminMutation(orderId, actorUserId, "A refund needs a reference (e.g. the Stripe refund id).");

        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null) return RefuseAdminMutation(orderId, actorUserId, "Order not found.");
        if (order.TeamId is not null)
            return RefuseAdminMutation(orderId, actorUserId, "Team orders are non-billable.");

        externalRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (externalRef is { Length: > 200 })
            return RefuseAdminMutation(orderId, actorUserId, "Payment reference must be 200 characters or fewer.");
        if (notes is { Length: > 1000 })
            return RefuseAdminMutation(orderId, actorUserId, "Payment notes must be 1000 characters or fewer.");

        if (method == PaymentMethod.DepositReturn)
        {
            var depositTotal = BalanceCalculator.Compute(order, await LoadCurrentPricesAsync(ct)).DepositTotalEur;
            var alreadyReturned = order.Payments
                .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
                .Sum(p => p.AmountEur);
            var remaining = depositTotal - alreadyReturned;
            if (amountEur > remaining)
                return RefuseAdminMutation(orderId, actorUserId,
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
            ExternalRef = externalRef,
            Notes = notes,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = actorUserId,
        };
        await repo.AddPaymentAsync(payment, ct);
        await audit.LogAsync(
            StoreAuditActions.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            $"Recorded {method} of EUR {signed:0.00} on order {orderId}"
                + (payment.ExternalRef is null ? string.Empty : $" (ref {payment.ExternalRef})"),
            actorUserId, orderId, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

    /// <summary>
    /// Hard delete of a payment recorded in error. The row is gone for good, so the audit entry carries everything
    /// needed to reconstruct it.
    /// </summary>
    public async Task<AdminMutationResult> DeletePaymentAsync(
        Guid orderId, Guid paymentId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null) return RefuseAdminMutation(orderId, actorUserId, "Order not found.");
        var payment = order.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (payment is null) return RefuseAdminMutation(orderId, actorUserId, "Payment not found on this order.");

        await repo.DeletePaymentAsync(paymentId, ct);
        await audit.LogAsync(
            StoreAuditActions.StorePaymentDeleted, AuditEntityTypes.Payment, payment.Id,
            $"Deleted {payment.Method} payment of EUR {payment.AmountEur:0.00} ({payment.Status}) on order {orderId}, "
                + $"received {payment.ReceivedAt}, ref {payment.ExternalRef ?? "none"}, PI {payment.StripePaymentIntentId ?? "none"}, "
                + $"recorded by {payment.RecordedByUserId?.ToString() ?? "none"}, notes {payment.Notes ?? "none"}",
            actorUserId, orderId, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

    public async Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default)
    {
        var sessionsOrNull = await stripeService.ListStoreCheckoutSessionsAsync(ct);
        var stripeQueried = sessionsOrNull is not null;
        var sessions = sessionsOrNull ?? [];
        var recorded = await repo.GetRecordedStripePaymentsAsync(ct);
        var recordedByPi = recorded.ToDictionary(p => p.PaymentIntentId, p => p.Status, StringComparer.Ordinal);

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

        // Orphans only when Stripe was actually queried: an unreadable Stripe yields an empty session list, which
        // does not make every recorded payment an orphan.
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
            // Stripe says paid but the local row hasn't settled: the amount isn't in the balance yet, so it must not
            // present as plain "Recorded".
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

            await RecordStripePaymentAsync(orderId, pi, amount, ct: ct); // idempotent on PI id
            count++;
            total += amount;
        }

        if (count > 0)
        {
            await audit.LogAsync(
                StoreAuditActions.StorePaymentsReconciled, "Store", Guid.Empty,
                $"Reconciled {count} Stripe payment(s) totalling EUR {total:0.00} from the Store Stripe account",
                actorUserId);
        }

        return new StripeReconciliationResult(count, total);
    }

    public async Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default)
    {
        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
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

        // "paid" is a settled sync payment; "unpaid" is an async method with only the debit mandate captured. A mandate
        // isn't money: record it Pending so the balance ignores it until async_payment_succeeded.
        var status = string.Equals(session.PaymentStatus, "paid", StringComparison.Ordinal)
            ? PaymentStatus.Paid
            : PaymentStatus.Pending;

        try
        {
            await RecordStripePaymentCoreAsync(orderId, paymentIntentId, amountEur, status, ct);
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
    /// Idempotent: a re-delivered event finding the row already in the target state no-ops. A terminal event arriving
    /// before <c>completed</c> records the payment directly in its terminal state, so the later completed can neither
    /// lose settlement nor recreate a failed payment as Pending.
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
            if (session.OrderId is { } orderId
                && session.AmountEur is { } amountEur && amountEur > 0)
            {
                await RecordStripePaymentCoreAsync(orderId, paymentIntentId, amountEur, target, ct);
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
            return; // re-delivery of an already-applied transition

        if (existing.Status != PaymentStatus.Pending)
        {
            // A non-Pending, non-target state (a Failed row getting a late success, or vice versa) is anomalous: log and
            // leave it rather than guess.
            logger.LogWarning(
                "{Kind} for PI {PaymentIntentId} found payment in unexpected state {Status}; leaving unchanged.",
                evt.Kind, paymentIntentId, existing.Status);
            return;
        }

        await repo.UpdatePaymentStatusAsync(existing.Id, target, ct);
        var action = target == PaymentStatus.Paid ? StoreAuditActions.StorePaymentSettled : StoreAuditActions.StorePaymentFailed;
        var verb = target == PaymentStatus.Paid ? "settled" : "failed";
        await audit.LogAsync(
            action, AuditEntityTypes.Payment, existing.Id,
            $"Stripe payment of EUR {existing.AmountEur:0.00} {verb} on order {existing.OrderId} (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, AuditEntityTypes.Order);
    }

    /// <summary>
    /// Defensive: removes an orphan Pending row for an expired session. Unreachable in practice, since Pending rows come
    /// from <c>completed</c>. Paid and Failed payments are never touched.
    /// </summary>
    private async Task HandleCheckoutSessionExpiredAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Session is not { PaymentIntentId: { } paymentIntentId })
        {
            logger.LogDebug("checkout.session.expired event {EventId} has no PaymentIntentId; nothing to clean up.", evt.EventId);
            return;
        }

        var existing = await repo.GetPaymentByStripePaymentIntentIdAsync(paymentIntentId, ct);
        if (existing is null || existing.Status != PaymentStatus.Pending)
            return;

        await repo.DeletePaymentAsync(existing.Id, ct);
        await audit.LogAsync(
            StoreAuditActions.StorePaymentExpired, AuditEntityTypes.Payment, existing.Id,
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
        var totalsByOrder = PriceAll(campOrdersInYear.Concat(teamOrders), context);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name);

        var byCounterparty = new List<OrderSummaryDto>();

        foreach (var o in campOrdersInYear)
        {
            var totals = totalsByOrder[o.Id];
            var sid = o.CampSeasonId!.Value;
            var campName = seasonsForYear[sid].Name;
            byCounterparty.Add(new OrderSummaryDto(
                o.Id,
                OrderCounterpartyType.Camp,
                sid,
                campName,
                o.State,
                totals.TotalDueEur,
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
                totals.TotalDueEur,
                0m, // team orders never have payments
                0m));
        }

        // By-item aggregates camp AND team orders so suppliers see the full demand.
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

    private static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum);

    private Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct) => orderReader.LoadCurrentPricesAsync(ct);
}
