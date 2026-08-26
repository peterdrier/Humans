using System.Globalization;
using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Holded.Contracts;
using Humans.Shifts.Contracts;
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
    IBurnSettingsService burnSettings,
    IStripeService stripeService,
    IHoldedClient holdedClient,
    IOptions<StoreSectionOptions> options,
    ILogger<Service> logger) : IApplicationService
{
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

        foreach (var season in campSeasons)
        {
            // One order per camp-season; if legacy data has multiple, surface
            // only the highest-balance one and let the admin delete the rest.
            var allOrders = await GetOrdersForCampSeasonAsync(season.Id, ct);
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

        // Team counterparties — top-level departments only. The viewer's own
        // coordinated departments, or every department when a privileged reader.
        // Order is the controller / view's concern (memory/architecture/display-sort-in-controllers.md).
        var teams = await teamService.GetTeamsAsync(ct);
        var teamOrderPrices = await LoadCurrentPricesAsync(ct);
        foreach (var team in teams.Values
            .Where(t => t.ParentTeamId is null
                        && (allCounterparties
                            || (t.ManagementRoleHolderUserIds is not null
                                && t.ManagementRoleHolderUserIds.Contains(userId)))))
        {
            var existing = await repo.GetOrderForTeamAsync(team.Id, year, ct);
            IReadOnlyList<OrderDto> orders;
            if (existing is null)
            {
                orders = [];
            }
            else
            {
                var productIds = existing.Lines.Select(l => l.ProductId).Distinct().ToList();
                var productNames = await LoadProductNamesAsync(productIds, ct);
                orders = [await MapOrderAsync(existing, productNames, teamOrderPrices, ct)];
            }
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
            counterparties,
            counterparties.Count == 0 && !allCounterparties);
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
            var year = await GetCurrentEventYearAsync();
            catalog = (await GetActiveCatalogAsync(year, ct))
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
            ?? throw new InvalidOperationException($"Product {draft.Id} not found");

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
        catch (ArgumentException ex)
        {
            logger.LogWarning("Store catalog Save validation failed: {Reason}", ex.Message);
            return CatalogSaveResult.Failure(null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Store catalog Save rejected: {Reason}", ex.Message);
            return CatalogSaveResult.Failure(null, ex.Message);
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
            throw new ArgumentException("Product name is required", nameof(draft));
        if (draft.UnitPriceEur < 0m)
            throw new ArgumentException("Unit price cannot be negative", nameof(draft));
        if (draft.VatRatePercent < 0m)
            throw new ArgumentException("VAT rate cannot be negative", nameof(draft));
        if (draft.DepositAmountEur is < 0m)
            throw new ArgumentException("Deposit cannot be negative", nameof(draft));
        if (draft.HoldedRevenueAccountNum is { } account and (< 10_000_000 or > 99_999_999))
            throw new ArgumentException("Holded revenue account must be an 8-digit chart number", nameof(draft));
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

    public async Task<Guid> CreateOrderAsync(Guid campSeasonId, Guid actorUserId, CancellationToken ct = default)
    {
        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, ct)
            ?? throw new InvalidOperationException($"Camp season {campSeasonId} not found.");

        // No year filter: a CampSeason *is* a (camp, year) pair, so every order returned here
        // already belongs to season.Year — except a legacy row still at Year = 0, which an
        // `o.Year == season.Year` guard would wave through and hand the season a second order.
        var existing = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        if (existing.Count > 0)
            throw new InvalidOperationException($"Camp season {campSeasonId} already has a Store order.");

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
        return order.Id;
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

    public async Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await teamService.GetTeamAsync(teamId, ct)
            ?? throw new InvalidOperationException($"Team {teamId} not found.");
        if (team.ParentTeamId is not null)
            throw new InvalidOperationException("Team orders are restricted to departments (top-level teams).");

        var year = await GetCurrentEventYearAsync();

        var existing = await repo.GetOrderForTeamAsync(teamId, year, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Team {teamId} already has a Store order for {year}.");

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
        return order.Id;
    }

    public async Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var year = await GetCurrentEventYearAsync();
        var order = await repo.GetOrderForTeamAsync(teamId, year, ct);
        if (order is null) return null;
        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        return await MapOrderAsync(order, productNames, currentPrices, ct);
    }

    public async Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new ArgumentException("Qty must be positive", nameof(qty));

        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        if (order.State != OrderState.Open)
            throw new InvalidOperationException("Cannot add lines to an issued order");

        var product = await repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        if (!product.IsActive)
            throw new InvalidOperationException(
                $"Product '{product.Name}' has been deactivated and is no longer orderable");

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

        // Lazy backfill of Year on legacy camp orders that pre-date the Year column.
        if (order.Year == 0 && order.CampSeasonId is { } seasonIdForBackfill)
        {
            var season = await campService.GetCampSeasonByIdAsync(seasonIdForBackfill, ct);
            if (season is not null)
            {
                order.Year = season.Year;
                order.UpdatedAt = clock.GetCurrentInstant();
                await repo.UpdateOrderAsync(order, ct);
            }
        }

        await audit.LogAsync(
            AuditAction.StoreLineAdded, AuditEntityTypes.OrderLine, line.Id,
            $"Added {qty} × '{product.Name}' to order {order.Id}"
                + (deadlinePassed ? $" (past order deadline {product.OrderableUntil})" : string.Empty),
            actorUserId, order.Id, AuditEntityTypes.Order);
    }

    public async Task<MutationResult> AddLineWithResultAsync(
        Guid orderId,
        Guid productId,
        int qty,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await AddLineAsync(orderId, productId, qty, actorUserId, ct);
            return MutationResult.Success;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("AddLine validation failed for order {OrderId}: {Reason}", orderId, ex.Message);
            return MutationResult.Failure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("AddLine rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return MutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default)
    {
        var ctx = await repo.GetLineWithOrderAndProductAsync(lineId, ct)
            ?? throw new InvalidOperationException($"Line {lineId} not found");

        if (ctx.OrderId != orderId)
            throw new InvalidOperationException($"Line {lineId} does not belong to order {orderId}");

        if (ctx.OrderState != OrderState.Open)
            throw new InvalidOperationException("Cannot remove lines from an issued order");

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
    }

    public async Task<MutationResult> RemoveLineWithResultAsync(
        Guid orderId,
        Guid lineId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await RemoveLineAsync(orderId, lineId, actorUserId, ct);
            return MutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("RemoveLine rejected for line {LineId}: {Reason}", lineId, ex.Message);
            return MutationResult.Failure(ex.Message);
        }
    }

    public async Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        EnsureBillable(order);

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
    }

    public async Task<MutationResult> UpdateCounterpartyWithResultAsync(
        Guid orderId,
        OrderCounterpartyInput input,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateCounterpartyAsync(orderId, input, actorUserId, ct);
            return MutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("UpdateCounterparty rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return MutationResult.Failure(ex.Message);
        }
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }

    /// <summary>Returns the active event's catalog year, falling back to the current UTC year before it exists.</summary>
    private async Task<int> GetCurrentEventYearAsync()
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }

    public Task<string> CreateStripeCheckoutSessionAsync(
        OrderDto order,
        decimal amountEur,
        string returnUrl,
        CancellationToken ct = default)
    {
        if (order.CounterpartyType == OrderCounterpartyType.Team)
            throw new InvalidOperationException("Team orders are non-billable.");

        if (!stripeService.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe is not configured for this environment. Contact an admin.");

        if (amountEur <= 0)
            throw new InvalidOperationException("Payment amount must be greater than zero.");

        if (amountEur > order.BalanceEur)
            throw new InvalidOperationException($"Payment amount cannot exceed the outstanding balance (EUR {order.BalanceEur:0.00}).");

        if (order.Payments.Any(p => p.Status == PaymentStatus.Pending))
            throw new InvalidOperationException("A payment on this order is pending settlement. Wait for it to clear or fail before paying again.");

        var description = $"Nobodies Collective - {order.CounterpartyName ?? "Camp order"}";

        return stripeService.CreateCheckoutSessionAsync(
            storeOrderId: order.Id,
            amountEur: amountEur,
            successUrl: returnUrl,
            cancelUrl: returnUrl,
            customerEmail: order.CounterpartyEmail,
            lineItemDescription: description,
            ct: ct);
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
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await repo.AddPaymentAsync(payment, ct);
        var settlement = status == PaymentStatus.Pending
            ? "Pending Stripe payment (mandate captured, not yet cleared)"
            : "Recorded Stripe payment";
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            $"{settlement} of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, AuditEntityTypes.Order);
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
            return paymentStatus == PaymentStatus.Pending
                ? StripeReconciliationStatus.RecordedPending
                : StripeReconciliationStatus.Recorded;
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
        }
    }

    /// <summary>
    /// Transitions the <see cref="PaymentStatus.Pending"/> payment behind an async Checkout
    /// event to its settled state — <see cref="PaymentStatus.Paid"/> on
    /// <c>async_payment_succeeded</c>, <see cref="PaymentStatus.Failed"/> on
    /// <c>async_payment_failed</c>. Idempotent: a re-delivered event that finds the row already in
    /// the target state is a no-op. Out-of-order tolerance: if the success event arrives before
    /// <c>completed</c> (no row yet), the payment is recorded directly as Paid so settled money is
    /// never lost; a failure with no row is a no-op (no money was ever pending here).
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
            // Out-of-order: the settlement event beat checkout.session.completed. Record the money
            // now (Paid) so it isn't lost; the later completed event no-ops on the unique PI. A
            // failure with no pending row means nothing was ever owed here — ignore it.
            if (target == PaymentStatus.Paid
                && session.OrderId is { } orderId
                && session.AmountEur is { } amountEur && amountEur > 0)
            {
                await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, PaymentStatus.Paid, ct);
                logger.LogInformation(
                    "Async {Kind} arrived before completed for order {OrderId} (PI {PaymentIntentId}); recorded settled payment directly.",
                    evt.Kind, orderId, paymentIntentId);
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

    /// <summary>
    /// Issues the camp order's consolidated Holded sales document and freezes the order.
    ///
    /// Pipeline: freeze the line snapshots at today's catalog price (#816) → resolve each
    /// product's revenue account → build the lines (goods at the product's VAT rate, deposits
    /// tax-0 to the fianzas liability account) → create the document and <b>approve</b> it (a
    /// draft books nothing) → write <c>store_invoices</c> with both payloads and flip the order
    /// to <see cref="OrderState.InvoiceIssued"/> in one save.
    ///
    /// Document kind: a full <i>factura</i> when counterparty details are on file, a
    /// <i>factura simplificada</i> (sales receipt) otherwise. Above
    /// <see cref="StoreSectionOptions.SimplifiedInvoiceThresholdEur"/> a receipt is not legal,
    /// so a counterparty-less order that large is refused rather than downgraded.
    ///
    /// Idempotent on both sides: an order that already carries an <c>IssuedInvoiceId</c> throws
    /// without calling Holded, and — because a document approved by an attempt that then failed
    /// locally leaves no trace here — Holded is searched for a document already tagged with this
    /// order before anything is created. A match is adopted, never re-issued: a second approved
    /// factura is a real legal document, correctable only by a factura rectificativa. A recovered
    /// document is checked against the order's current totals first (divergence refuses loudly)
    /// and approved if the earlier attempt died between create and approve.
    /// </summary>
    [ExternalWrite]
    public async Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");
        EnsureBillable(order);

        // Idempotency guard — deliberately before any Holded call, so a double-submit cannot
        // create a second document.
        if (order.IssuedInvoiceId is not null || order.State != OrderState.Open)
            throw new InvalidOperationException("This order has already been invoiced.");

        if (order.Lines.Count == 0)
            throw new InvalidOperationException("Cannot invoice an order with no lines.");

        if (!holdedClient.IsConfigured)
            throw new InvalidOperationException(
                "Holded is not configured in this environment, so no invoice can be issued.");

        // Freeze seam (#816): rewrite each snapshot from the live catalog before issuing, so the
        // document and the order agree forever after. BalanceCalculator already computed the
        // effective prices for an Open order — reuse them rather than re-deriving.
        var totals = BalanceCalculator.Compute(order, await LoadCurrentPricesAsync(ct));
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            line.UnitPriceSnapshot = t.EffectiveUnitPrice;
            line.VatRateSnapshot = t.EffectiveVatRate;
            line.DepositAmountSnapshot = t.EffectiveDeposit;
        }

        // The local guard above cannot see a document approved by an attempt that then failed
        // before its save, so ask Holded before creating anything. Read-only, and it runs ahead of
        // the line-account resolution so that a catalog edited in between cannot block the
        // reconciliation of a document that already exists.
        var tag = OrderDocumentTag(order.Id);
        if (await FindAlreadyIssuedDocumentAsync(tag, ct) is { } alreadyIssued)
        {
            var (adoptedKind, adoptedDoc) = alreadyIssued;
            // Before anything is written, and before the order is frozen against it: the order
            // stayed Open, so it could have been edited or repriced since that document was
            // created. Divergence is a human's problem, not something to reconcile silently.
            EnsureRecoveredDocumentMatches(order, adoptedDoc, totals);
            if (adoptedDoc.IsDraft == true)
            {
                // Creation succeeded and approval did not. A draft books no revenue and carries no
                // sequential number, so freezing the order against it would leave the order
                // invoiced with no legal invoice behind it — finish the issuance instead.
                await holdedClient.ApproveSalesDocumentAsync(adoptedKind, adoptedDoc.Id, CancellationToken.None);
                adoptedDoc = await holdedClient.GetSalesDocumentAsync(
                    adoptedKind, adoptedDoc.Id, CancellationToken.None);
            }
            await CommitInvoiceAsync(
                order, adoptedDoc,
                JsonSerializer.Serialize(new { kind = adoptedKind.ToString(), adopted = true, tag }),
                actorUserId,
                $"Adopted Holded {DocumentKindName(adoptedKind)} {adoptedDoc.DocNumber} "
                + $"(EUR {adoptedDoc.Total:0.00}), already issued for order {order.Id} by an earlier "
                + "attempt that failed before saving — no second document was created",
                ct);
            return;
        }

        var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
        if (totalDue <= 0m)
            throw new InvalidOperationException("Cannot invoice an order with a zero total.");

        var products = (await repo.GetProductsByIdsAsync(
                order.Lines.Select(l => l.ProductId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);

        var documentLines = await BuildInvoiceLinesAsync(order, totalsByLine, products, ct);

        // A receipt carries no counterparty, so it is only lawful below the simplified-invoice
        // threshold. Above it the details are mandatory — refuse rather than silently issue the
        // wrong document type.
        var counterparty = ResolveInvoiceCounterparty(order);
        var kind = counterparty is null
            ? HoldedSalesDocumentKind.SalesReceipt
            : HoldedSalesDocumentKind.Invoice;
        if (counterparty is null && totalDue > options.Value.SimplifiedInvoiceThresholdEur)
            throw new InvalidOperationException(
                $"An order of EUR {totalDue:0.00} needs a full factura: fill in the counterparty's "
                + "name, address and tax id (or passport number) first.");

        var displayName = await ResolveCounterpartyDisplayNameAsync(order, ct);
        string? contactId = null;
        if (counterparty is not null)
        {
            // Not the request token: everything from here on writes to Holded, and a half-created
            // contact/document has no local compensation (memory/architecture/cancellation-token-propagation.md).
            contactId = await holdedClient.UpsertContactAsync(
                new HoldedContactInput
                {
                    Name = counterparty.Name,
                    Type = "client",
                    TaxCode = counterparty.TaxId,
                    Address = counterparty.Address,
                    CountryCode = counterparty.CountryCode,
                    Email = counterparty.Email,
                },
                CancellationToken.None);
        }

        var input = new HoldedSalesDocumentInput
        {
            ContactId = contactId,
            ContactName = counterparty?.Name ?? displayName,
            Date = clock.GetCurrentInstant(),
            Description = $"Nobodies Collective store — {displayName}",
            Notes = $"Humans store order {order.Id}",
            Tags = [tag],
            Lines = documentLines,
        };

        var docId = await holdedClient.CreateSalesDocumentAsync(kind, input, CancellationToken.None);
        // A draft books no revenue, so approval is part of issuance, not a later step.
        await holdedClient.ApproveSalesDocumentAsync(kind, docId, CancellationToken.None);
        var document = await holdedClient.GetSalesDocumentAsync(kind, docId, CancellationToken.None);

        await CommitInvoiceAsync(
            order, document,
            JsonSerializer.Serialize(new { kind = kind.ToString(), input }),
            actorUserId,
            $"Issued Holded {DocumentKindName(kind)} {document.DocNumber} "
            + $"for EUR {totalDue:0.00} on order {order.Id}",
            ct);
    }

    /// <summary>
    /// Refuses adoption when the recovered document no longer describes this order. The order stays
    /// <c>Open</c> after a failed attempt, so its lines can be edited and its catalog prices can
    /// move before the retry — adopting on the tag alone would freeze the order against a document
    /// that says something else, permanently. Correcting an issued document is a factura
    /// rectificativa, which is a human decision, so this fails loudly rather than choosing a side.
    /// </summary>
    private static void EnsureRecoveredDocumentMatches(
        Order order, HoldedSalesDocumentDto document, BalanceCalculator.Result totals)
    {
        // Deposits are ordinary tax-0 lines on the document, so they land in Holded's subtotal.
        var expectedSubtotal = decimal.Round(totals.LinesSubtotalEur + totals.DepositTotalEur, 2);
        var expectedTax = decimal.Round(totals.VatTotalEur, 2);
        var expectedTotal = decimal.Round(expectedSubtotal + expectedTax, 2);
        if (decimal.Round(document.Subtotal, 2) == expectedSubtotal
            && decimal.Round(document.Tax, 2) == expectedTax
            && decimal.Round(document.Total, 2) == expectedTotal)
            return;

        var name = string.IsNullOrEmpty(document.DocNumber) ? document.Id : document.DocNumber;
        throw new InvalidOperationException(
            $"Holded already holds document {name} for order {order.Id}, but it no longer matches "
            + $"the order: the document reads EUR {document.Subtotal:0.00} + {document.Tax:0.00} VAT "
            + $"= {document.Total:0.00}, while the order now totals EUR {expectedSubtotal:0.00} + "
            + $"{expectedTax:0.00} VAT = {expectedTotal:0.00}. The order was changed after that "
            + "document was created. Resolve it by hand — correcting an issued document is a factura "
            + "rectificativa, and no second document will be issued in the meantime.");
    }

    /// <summary>The tag every store sales document carries. Holded's list endpoints return
    /// <c>tags</c> but not <c>notes</c>, so this — not the human-readable note beside it — is what
    /// makes a document findable by the order it was issued for.</summary>
    private static string OrderDocumentTag(Guid orderId) => $"humans-order-{orderId}";

    private static string DocumentKindName(HoldedSalesDocumentKind kind) =>
        kind == HoldedSalesDocumentKind.Invoice ? "factura" : "factura simplificada";

    /// <summary>
    /// The sales document a previous attempt already created for this order, or null. Both kinds
    /// are searched in full: the counterparty details decide the kind and can be filled in between
    /// two attempts, so the earlier document may be of the other kind — and adopting it is still
    /// right, because what must not happen is a second approved document. When several match, an
    /// already-approved one wins over a draft: approving the draft beside it would book the revenue
    /// twice and hand out a second legal number.
    /// </summary>
    private async Task<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)?>
        FindAlreadyIssuedDocumentAsync(string tag, CancellationToken ct)
    {
        var found = new List<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)>();
        foreach (var kind in new[] { HoldedSalesDocumentKind.Invoice, HoldedSalesDocumentKind.SalesReceipt })
        {
            foreach (var id in await holdedClient.FindSalesDocumentIdsByTagAsync(kind, tag, ct))
                found.Add((kind, await holdedClient.GetSalesDocumentAsync(kind, id, ct)));
        }
        if (found.Count == 0) return null;
        if (found.Count > 1)
            // The duplicate this guard exists to prevent has already happened — only a factura
            // rectificativa clears it. Adopting one still stops a third being issued.
            logger.LogWarning(
                "Holded holds {Count} documents tagged {Tag}: {Ids}. Adopting an approved one if there is any.",
                found.Count, tag, string.Join(", ", found.Select(f => $"{f.Kind}:{f.Document.Id}")));

        var approved = found.FirstOrDefault(f => f.Document.IsDraft != true);
        return approved.Document is not null ? approved : found[0];
    }

    /// <summary>Writes the <c>store_invoices</c> row and the frozen order in one save, then audits.
    /// Shared by first issuance and by adoption of an already-issued document — the two differ only
    /// in the request payload they can honestly record and in what the audit line says.</summary>
    private async Task CommitInvoiceAsync(
        Order order,
        HoldedSalesDocumentDto document,
        string requestPayload,
        Guid actorUserId,
        string auditDetail,
        CancellationToken ct)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            HoldedDocId = document.Id,
            HoldedDocNumber = document.DocNumber,
            IssuedAt = clock.GetCurrentInstant(),
            IssuedByUserId = actorUserId,
            RequestPayload = requestPayload,
            ResponsePayload = document.RawJson,
        };

        order.State = OrderState.InvoiceIssued;
        order.IssuedInvoiceId = invoice.Id;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveIssuedInvoiceAsync(invoice, order, ct);

        await audit.LogAsync(
            AuditAction.StoreInvoiceIssued, AuditEntityTypes.Invoice, invoice.Id,
            auditDetail, actorUserId,
            order.Id, AuditEntityTypes.Order);
    }

    /// <summary>
    /// One document line per order line, plus a separate VAT-free line for each line that carries
    /// a deposit. Refundable deposits are a liability, not income, so they post tax-0 to the
    /// configured fianzas account instead of the item's revenue account.
    /// </summary>
    private async Task<IReadOnlyList<HoldedSalesDocumentLineInput>> BuildInvoiceLinesAsync(
        Order order,
        IReadOnlyDictionary<Guid, BalanceCalculator.LineTotals> totalsByLine,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken ct)
    {
        var missingAccount = order.Lines
            .Select(l => products.GetValueOrDefault(l.ProductId))
            .Where(p => p?.HoldedRevenueAccountNum is null)
            .Select(p => p?.Name ?? "(unknown product)")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missingAccount.Count > 0)
            throw new InvalidOperationException(
                "These catalog items have no Holded revenue account yet: "
                + string.Join(", ", missingAccount)
                + ". Set it on /Store/Admin/Catalog before issuing.");

        var needsDepositAccount = order.Lines.Any(l => totalsByLine[l.Id].DepositEur > 0m);
        var depositAccountNum = options.Value.DepositLiabilityAccountNum;
        if (needsDepositAccount && depositAccountNum is null)
            throw new InvalidOperationException(
                "This order carries refundable deposits but no deposit liability account is "
                + "configured (Store:DepositLiabilityAccountNum). Deposits are not income and "
                + "must not be booked to a revenue account.");

        // Holded's items[].account is the chart account's *id*, not its 8-digit number, so the
        // numbers StoreAdmin holds are resolved against the live chart on every issue — a fresh
        // read also picks up an account Acountax created minutes ago.
        var wanted = order.Lines
            .Select(l => products[l.ProductId].HoldedRevenueAccountNum!.Value)
            .Concat(needsDepositAccount ? [depositAccountNum!.Value] : Array.Empty<int>())
            .ToHashSet();
        var accountIdsByNum = (await holdedClient.ListAccountingAccountsAsync(ct))
            .Where(a => wanted.Contains(a.Number))
            .ToDictionary(a => a.Number, a => a.Id);

        var unknown = wanted.Where(n => !accountIdsByNum.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                "These accounts do not exist in Holded's chart of accounts: "
                + string.Join(", ", unknown.OrderBy(n => n))
                + ". Ask Acountax to create them, then re-issue.");

        var lines = new List<HoldedSalesDocumentLineInput>();
        foreach (var line in order.Lines)
        {
            var product = products[line.ProductId];
            var t = totalsByLine[line.Id];
            lines.Add(new HoldedSalesDocumentLineInput
            {
                Name = product.Name,
                Units = line.Qty,
                Price = t.EffectiveUnitPrice,
                Taxes = [SalesTaxKey(t.EffectiveVatRate)],
                AccountId = accountIdsByNum[product.HoldedRevenueAccountNum!.Value],
            });

            if (t.EffectiveDeposit is not { } depositPerUnit || depositPerUnit <= 0m) continue;
            lines.Add(new HoldedSalesDocumentLineInput
            {
                Name = $"{product.Name} — refundable deposit",
                Units = line.Qty,
                Price = depositPerUnit,
                Taxes = [SalesTaxKey(0m)],
                AccountId = accountIdsByNum[depositAccountNum!.Value],
            });
        }
        return lines;
    }

    /// <summary>
    /// Holded's sales tax key for a VAT rate: 21 → <c>s_iva_21</c>, 7.5 → <c>s_iva_75</c>,
    /// 0 → <c>s_iva_0</c> (the decimal separator is dropped, not rounded away).
    /// </summary>
    private static string SalesTaxKey(decimal vatRatePercent) =>
        "s_iva_" + vatRatePercent.ToString("0.##", CultureInfo.InvariantCulture).Replace(".", "", StringComparison.Ordinal);

    /// <summary>
    /// The identified counterparty a full factura needs, or null when the order does not carry
    /// enough to identify one. Spanish law wants name + address + a tax id; a foreign camp's
    /// home-country tax id or passport number stands in for a NIF, so the field is only ever
    /// checked for presence.
    /// </summary>
    private static InvoiceCounterparty? ResolveInvoiceCounterparty(Order order) =>
        string.IsNullOrWhiteSpace(order.CounterpartyName)
        || string.IsNullOrWhiteSpace(order.CounterpartyAddress)
        || string.IsNullOrWhiteSpace(order.CounterpartyVatId)
            ? null
            : new InvoiceCounterparty(
                order.CounterpartyName.Trim(),
                order.CounterpartyVatId.Trim(),
                order.CounterpartyAddress.Trim(),
                order.CounterpartyCountryCode?.Trim(),
                order.CounterpartyEmail?.Trim());

    private sealed record InvoiceCounterparty(
        string Name, string TaxId, string Address, string? CountryCode, string? Email);

    public async Task<SummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
    {
        var seasonsForYear = (await campService.GetCampsForYearAsync(year, ct))
            .SelectMany(camp => camp.Seasons.Where(season => season.Year == year))
            .ToDictionary(season => season.Id);
        var products = await repo.GetAllProductsForYearAsync(year, ct);

        var campOrders = seasonsForYear.Count == 0
            ? Array.Empty<Order>()
            : await repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                seasonsForYear.Keys.ToList(), ct);

        var campOrdersInYear = campOrders
            .Where(o => o.CampSeasonId is { } sid && seasonsForYear.ContainsKey(sid))
            .ToList();

        // Team orders — load departments the user *exists on the platform* (no user filter here:
        // admin summary reflects all departments). Filter to ParentTeamId is null.
        var allTeams = await teamService.GetTeamsAsync(ct);
        var departmentIds = allTeams.Values
            .Where(t => t.ParentTeamId is null)
            .Select(t => t.Id)
            .ToList();
        var teamOrders = await repo.GetOrdersForTeamsWithLinesAsync(departmentIds, year, ct);
        var teamNames = allTeams.Values.ToDictionary(t => t.Id, t => t.Name);

        var productNames = products.ToDictionary(p => p.Id, p => p.Name);

        // Reprice Open orders to the live catalog, exactly like the order page
        // (MapOrderAsync) — summing raw snapshots here made summary totals drift
        // from order totals whenever a catalog price changed after lines were added.
        // Priced from the requested year's products (already loaded above), not the
        // active event's catalog — historical summaries must not reprice against a
        // later year's prices.
        var currentPrices = products.ToDictionary(
            p => p.Id,
            p => new BalanceCalculator.ProductPrice(p.UnitPriceEur, p.VatRatePercent, p.DepositAmountEur));
        var totalsByOrder = campOrdersInYear
            .Concat(teamOrders)
            .ToDictionary(o => o.Id, o => BalanceCalculator.Compute(o, currentPrices));

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

    private static ProductDto MapProduct(Product p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive, p.HoldedRevenueAccountNum);

    /// <summary>Display names for the given product ids, live or deactivated, any year.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadProductNamesAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        (await repo.GetProductsByIdsAsync(productIds, ct)).ToDictionary(p => p.Id, p => p.Name);

    /// <summary>
    /// Loads the current catalog price components (incl. deactivated products) for the active
    /// event's year, keyed by product id, so Open orders reprice to the live price. The org runs
    /// one event year, so a single catalog year drives repricing rather than each order's
    /// <c>Year</c> — which is also why legacy <c>store_orders</c> rows still at <c>Year = 0</c>
    /// reprice correctly.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        CancellationToken ct)
    {
        var catalogYear = await GetCurrentEventYearAsync();
        var prices = new Dictionary<Guid, BalanceCalculator.ProductPrice>();
        foreach (var product in await repo.GetAllProductsForYearAsync(catalogYear, ct))
            prices[product.Id] = new BalanceCalculator.ProductPrice(
                product.UnitPriceEur, product.VatRatePercent, product.DepositAmountEur);
        return prices;
    }

    private async Task<OrderDto> MapOrderAsync(
        Order o,
        IReadOnlyDictionary<Guid, string> productNames,
        IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice> currentPrices,
        CancellationToken ct)
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
                p.AmountEur, p.Method, p.Status, p.StripePaymentIntentId, p.ExternalRef, p.ReceivedAt, p.Notes))
            .ToList();

        var counterpartyType = o.TeamId is not null
            ? OrderCounterpartyType.Team
            : OrderCounterpartyType.Camp;

        var displayName = await ResolveCounterpartyDisplayNameAsync(o, ct);

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

    private async Task<string> ResolveCounterpartyDisplayNameAsync(Order o, CancellationToken ct)
    {
        if (o.TeamId is { } tid)
        {
            var team = await teamService.GetTeamAsync(tid, ct);
            return team?.Name ?? "(unknown team)";
        }
        if (o.CampSeasonId is { } sid)
        {
            var season = await campService.GetCampSeasonByIdAsync(sid, ct);
            return season?.Name ?? "(unknown camp)";
        }
        return "(unknown)";
    }

    private static void EnsureBillable(Order order)
    {
        if (order.TeamId is not null)
            throw new InvalidOperationException("Team orders are non-billable.");
    }
}
