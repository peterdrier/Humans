using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Services.Store;

public class StoreService(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampServiceRead campService,
    ITeamServiceRead teamService,
    IClock clock,
    IShiftManagementService shifts,
    IStripeService stripeService,
    ILogger<StoreService> logger) : IStoreService
{
    public async Task<StoreIndexData> GetIndexDataAsync(
        Guid userId,
        bool isPrivilegedReader,
        CancellationToken ct = default)
    {
        var activeEvent = await shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var catalog = (await GetActiveCatalogAsync(year, ct))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var counterparties = new List<StoreCounterpartyOrders>();

        // Camp counterparties: the one camp the viewer leads, or every camp's
        // season for the year when the viewer is a privileged reader.
        var campSeasons = new List<CampSeasonInfo>();
        foreach (var camp in await campService.GetCampsForYearAsync(year, ct))
        {
            if (isPrivilegedReader)
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
            counterparties.Add(new StoreCounterpartyOrders(
                StoreOrderCounterpartyType.Camp,
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
                        && (isPrivilegedReader
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
                var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
                orders = [await MapOrderAsync(existing, productNames, teamOrderPrices, ct)];
            }
            counterparties.Add(new StoreCounterpartyOrders(
                StoreOrderCounterpartyType.Team,
                team.Id,
                team.Name,
                year,
                orders));
        }

        return new StoreIndexData(
            year,
            catalog,
            counterparties,
            counterparties.Count == 0 && !isPrivilegedReader);
    }

    public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
    {
        var products = await repo.GetActiveProductsForYearAsync(year, ct);
        return products
            .Select(MapProduct)
            .ToList();
    }

    public async Task<StoreOrderPageData> GetOrderPageDataAsync(
        OrderDto order,
        bool canEdit,
        bool canPayAuthorized,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProductDto> catalog = [];
        if (canEdit)
        {
            var activeEvent = await shifts.GetActiveAsync();
            var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
            catalog = (await GetActiveCatalogAsync(year, ct))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        var priceChanges = await LoadOrderPriceChangesAsync(order, ct);

        // A pending async payment (e.g. SEPA mandate captured, not yet cleared) is excluded from
        // BalanceEur, so without this guard the full balance would stay payable a second time
        // while the mandate settles — a double-charge window.
        var hasPendingPayment = order.Payments.Any(p => p.Status == StorePaymentStatus.Pending);

        return new StoreOrderPageData(
            order,
            catalog,
            order.CounterpartyDisplayName,
            canEdit,
            canPayAuthorized && order.BalanceEur > 0 && !hasPendingPayment && order.CounterpartyType == StoreOrderCounterpartyType.Camp,
            stripeService.IsStoreCheckoutConfigured,
            priceChanges);
    }

    /// <summary>
    /// Price-change audit events (<see cref="AuditAction.StoreProductPriceChanged"/>) for the
    /// products on this order, recorded since the order was created — the order page's
    /// "price changes" view (#816). Reuses the existing per-entity audit query; the product
    /// count per order is tiny.
    /// </summary>
    private async Task<IReadOnlyList<AuditLogEntrySnapshot>> LoadOrderPriceChangesAsync(OrderDto order, CancellationToken ct)
    {
        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        if (productIds.Count == 0)
            return [];

        var changes = new List<AuditLogEntrySnapshot>();
        foreach (var productId in productIds)
        {
            var entries = await audit.GetFilteredEntriesAsync(
                entityType: nameof(StoreProduct),
                entityId: productId,
                actions: [AuditAction.StoreProductPriceChanged],
                limit: 50,
                ct: ct);
            changes.AddRange(entries.Where(e => e.OccurredAt >= order.CreatedAt));
        }
        return changes.OrderByDescending(e => e.OccurredAt).ToList();
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
        var product = new StoreProduct
        {
            Id = Guid.NewGuid(),
            Year = draft.Year,
            Name = draft.Name.Trim(),
            Description = draft.Description,
            UnitPriceEur = draft.UnitPriceEur,
            VatRatePercent = draft.VatRatePercent,
            DepositAmountEur = draft.DepositAmountEur,
            OrderableUntil = draft.OrderableUntil,
            IsActive = draft.IsActive,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductCreated, nameof(StoreProduct), product.Id,
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
        product.OrderableUntil = draft.OrderableUntil;
        product.IsActive = draft.IsActive;
        product.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateProductAsync(product, ct);
        await audit.LogAsync(
            AuditAction.StoreProductUpdated, nameof(StoreProduct), product.Id,
            $"Updated store product '{product.Name}'",
            actorUserId);

        // Dedicated, queryable price-change event for the order-page audit view (#816).
        if (oldPrice != draft.UnitPriceEur)
            await audit.LogAsync(
                AuditAction.StoreProductPriceChanged, nameof(StoreProduct), product.Id,
                $"Price for {product.Name} changed from {oldPrice:0.00} to {draft.UnitPriceEur:0.00}",
                actorUserId);
    }

    public async Task<StoreCatalogSaveResult> SaveProductWithResultAsync(
        StoreProductSaveRequest request,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var parseResult = LocalDatePattern.Iso.Parse(request.OrderableUntil ?? string.Empty);
        if (!parseResult.Success)
            return StoreCatalogSaveResult.Failure(nameof(request.OrderableUntil), "Invalid date - use YYYY-MM-DD.");

        var dto = new ProductDto(
            request.Id ?? Guid.Empty,
            request.Year,
            request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.UnitPriceEur,
            request.VatRatePercent,
            request.DepositAmountEur,
            parseResult.Value,
            request.IsActive);

        try
        {
            if (request.Id is null)
            {
                await CreateProductAsync(dto, actorUserId, ct);
                return StoreCatalogSaveResult.Success(created: true);
            }

            await UpdateProductAsync(dto, actorUserId, ct);
            return StoreCatalogSaveResult.Success(created: false);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("Store catalog Save validation failed: {Reason}", ex.Message);
            return StoreCatalogSaveResult.Failure(null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Store catalog Save rejected: {Reason}", ex.Message);
            return StoreCatalogSaveResult.Failure(null, ex.Message);
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
            AuditAction.StoreProductDeactivated, nameof(StoreProduct), productId,
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
    }

    public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var orders = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        var productIds = orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct().ToList();
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
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
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        return await MapOrderAsync(o, productNames, currentPrices, ct);
    }

    public async Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
    {
        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, ct)
            ?? throw new InvalidOperationException($"Camp season {campSeasonId} not found.");

        var existing = await repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
        if (existing.Any(o => o.Year == season.Year))
            throw new InvalidOperationException($"Camp season {campSeasonId} already has a Store order for {season.Year}.");

        var now = clock.GetCurrentInstant();
        var order = new StoreOrder
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            TeamId = null,
            Year = season.Year,
            State = StoreOrderState.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderCreated, nameof(StoreOrder), order.Id,
            $"Created store order for camp season {campSeasonId}" +
            (string.IsNullOrWhiteSpace(label) ? string.Empty : $" — '{label}'"),
            actorUserId);
        return order.Id;
    }

    public async Task DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        var currentPrices = await LoadCurrentPricesAsync(ct);
        var balance = BalanceCalculator.Compute(order, currentPrices).BalanceEur;
        if (balance != 0m)
            throw new InvalidOperationException(
                $"Order {orderId} has a non-zero balance (EUR {balance:0.00}); only zero-balance orders may be deleted.");

        await repo.DeleteOrderAsync(orderId, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderDeleted, nameof(StoreOrder), orderId,
            $"Deleted store order {orderId}",
            actorUserId);
    }

    public async Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await teamService.GetTeamAsync(teamId, ct)
            ?? throw new InvalidOperationException($"Team {teamId} not found.");
        if (team.ParentTeamId is not null)
            throw new InvalidOperationException("Team orders are restricted to departments (top-level teams).");

        var activeEvent = await shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;

        var existing = await repo.GetOrderForTeamAsync(teamId, year, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Team {teamId} already has a Store order for {year}.");

        var now = clock.GetCurrentInstant();
        var order = new StoreOrder
        {
            Id = Guid.NewGuid(),
            CampSeasonId = null,
            TeamId = teamId,
            Year = year,
            State = StoreOrderState.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            AuditAction.StoreOrderCreated, nameof(StoreOrder), order.Id,
            $"Created store order for team '{team.Name}' ({year})",
            actorUserId);
        return order.Id;
    }

    public async Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var activeEvent = await shifts.GetActiveAsync();
        var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var order = await repo.GetOrderForTeamAsync(teamId, year, ct);
        if (order is null) return null;
        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(ct);
        return await MapOrderAsync(order, productNames, currentPrices, ct);
    }

    public async Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new ArgumentException("Qty must be positive", nameof(qty));

        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found");

        if (order.State != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot add lines to an issued order");

        var product = await repo.GetProductByIdAsync(productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        if (!product.IsActive)
            throw new InvalidOperationException(
                $"Product '{product.Name}' has been deactivated and is no longer orderable");

        // OrderableUntil is gated by StoreOrderAuthorizationHandler (Store admins exempt,
        // everyone else denied) — the auth-free service only annotates the audit entry.
        var today = await TodayInEventZoneAsync();
        var deadlinePassed = today > product.OrderableUntil;

        var line = new StoreOrderLine
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
            AuditAction.StoreLineAdded, nameof(StoreOrderLine), line.Id,
            $"Added {qty} × '{product.Name}' to order {order.Id}"
                + (deadlinePassed ? $" (past order deadline {product.OrderableUntil})" : string.Empty),
            actorUserId, order.Id, nameof(StoreOrder));
    }

    public async Task<StoreMutationResult> AddLineWithResultAsync(
        Guid orderId,
        Guid productId,
        int qty,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await AddLineAsync(orderId, productId, qty, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("AddLine validation failed for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("AddLine rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveLineAsync(Guid orderId, Guid lineId, Guid actorUserId, CancellationToken ct = default)
    {
        var ctx = await repo.GetLineWithOrderAndProductAsync(lineId, ct)
            ?? throw new InvalidOperationException($"Line {lineId} not found");

        if (ctx.OrderId != orderId)
            throw new InvalidOperationException($"Line {lineId} does not belong to order {orderId}");

        if (ctx.OrderState != StoreOrderState.Open)
            throw new InvalidOperationException("Cannot remove lines from an issued order");

        // OrderableUntil is gated by StoreOrderAuthorizationHandler (Store admins exempt,
        // everyone else denied) — the auth-free service only annotates the audit entry.
        var today = await TodayInEventZoneAsync();
        var deadlinePassed = today > ctx.ProductOrderableUntil;

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            $"Removed line {lineId} from order {ctx.OrderId}"
                + (deadlinePassed ? $" (past order deadline {ctx.ProductOrderableUntil})" : string.Empty),
            actorUserId, ctx.OrderId, nameof(StoreOrder));
    }

    public async Task<StoreMutationResult> RemoveLineWithResultAsync(
        Guid orderId,
        Guid lineId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await RemoveLineAsync(orderId, lineId, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("RemoveLine rejected for line {LineId}: {Reason}", lineId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
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
            AuditAction.StoreCounterpartyEdited, nameof(StoreOrder), orderId,
            $"Updated counterparty on order {orderId}",
            actorUserId);
    }

    public async Task<StoreMutationResult> UpdateCounterpartyWithResultAsync(
        Guid orderId,
        OrderCounterpartyInput input,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateCounterpartyAsync(orderId, input, actorUserId, ct);
            return StoreMutationResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("UpdateCounterparty rejected for order {OrderId}: {Reason}", orderId, ex.Message);
            return StoreMutationResult.Failure(ex.Message);
        }
    }

    private async Task<LocalDate> TodayInEventZoneAsync()
    {
        var activeEvent = await shifts.GetActiveAsync();
        var tz = activeEvent is null
            ? DateTimeZone.Utc
            : DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId) ?? DateTimeZone.Utc;
        return clock.GetCurrentInstant().InZone(tz).Date;
    }

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public Task<string> CreateStripeCheckoutSessionAsync(
        OrderDto order,
        decimal amountEur,
        string returnUrl,
        CancellationToken ct = default)
    {
        if (order.CounterpartyType == StoreOrderCounterpartyType.Team)
            throw new InvalidOperationException("Team orders are non-billable.");

        if (!stripeService.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe is not configured for this environment. Contact an admin.");

        if (amountEur <= 0)
            throw new InvalidOperationException("Payment amount must be greater than zero.");

        if (amountEur > order.BalanceEur)
            throw new InvalidOperationException($"Payment amount cannot exceed the outstanding balance (EUR {order.BalanceEur:0.00}).");

        if (order.Payments.Any(p => p.Status == StorePaymentStatus.Pending))
            throw new InvalidOperationException("A payment on this order is pending settlement. Wait for it to clear or fail before paying again.");

        var description = $"Nobodies Collective - {order.CounterpartyName ?? "Camp order"}"
            + (string.IsNullOrWhiteSpace(order.Label) ? string.Empty : $" ({order.Label})");

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
        StorePaymentStatus status = StorePaymentStatus.Paid,
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

        var payment = new StorePayment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AmountEur = amountEur,
            Method = StorePaymentMethod.Stripe,
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await repo.AddPaymentAsync(payment, ct);
        var settlement = status == StorePaymentStatus.Pending
            ? "Pending Stripe payment (mandate captured, not yet cleared)"
            : "Recorded Stripe payment";
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, nameof(StorePayment), payment.Id,
            $"{settlement} of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, nameof(StoreOrder));
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
        StoreCheckoutSessionData s, OrderDto? order, IReadOnlyDictionary<string, StorePaymentStatus> recordedByPi)
    {
        if (!string.Equals(s.PaymentStatus, "paid", StringComparison.Ordinal))
            return StripeReconciliationStatus.Unpaid;
        if (s.PaymentIntentId is { } pi && recordedByPi.TryGetValue(pi, out var paymentStatus))
            // Stripe says paid but the local row hasn't settled — the amount is not in the
            // balance yet, so it must not present as plain "Recorded".
            return paymentStatus == StorePaymentStatus.Pending
                ? StripeReconciliationStatus.RecordedPending
                : StripeReconciliationStatus.Recorded;
        if (order is null || s.PaymentIntentId is null || order.CounterpartyType == StoreOrderCounterpartyType.Team)
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
                await TransitionAsyncPaymentAsync(evt, StorePaymentStatus.Paid, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
                await TransitionAsyncPaymentAsync(evt, StorePaymentStatus.Failed, ct);
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
            ? StorePaymentStatus.Paid
            : StorePaymentStatus.Pending;

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
    /// Transitions the <see cref="StorePaymentStatus.Pending"/> payment behind an async Checkout
    /// event to its settled state — <see cref="StorePaymentStatus.Paid"/> on
    /// <c>async_payment_succeeded</c>, <see cref="StorePaymentStatus.Failed"/> on
    /// <c>async_payment_failed</c>. Idempotent: a re-delivered event that finds the row already in
    /// the target state is a no-op. Out-of-order tolerance: if the success event arrives before
    /// <c>completed</c> (no row yet), the payment is recorded directly as Paid so settled money is
    /// never lost; a failure with no row is a no-op (no money was ever pending here).
    /// </summary>
    private async Task TransitionAsyncPaymentAsync(StoreCheckoutWebhookEvent evt, StorePaymentStatus target, CancellationToken ct)
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
            if (target == StorePaymentStatus.Paid
                && session.OrderId is { } orderId
                && session.AmountEur is { } amountEur && amountEur > 0)
            {
                await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, StorePaymentStatus.Paid, ct);
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

        if (existing.Status != StorePaymentStatus.Pending)
        {
            // A non-Pending, non-target state (e.g. a Failed row receiving a late success, or vice
            // versa) is anomalous; log and leave the recorded state untouched rather than guess.
            logger.LogWarning(
                "{Kind} for PI {PaymentIntentId} found payment in unexpected state {Status}; leaving unchanged.",
                evt.Kind, paymentIntentId, existing.Status);
            return;
        }

        await repo.UpdatePaymentStatusAsync(existing.Id, target, ct);
        var action = target == StorePaymentStatus.Paid ? AuditAction.StorePaymentSettled : AuditAction.StorePaymentFailed;
        var verb = target == StorePaymentStatus.Paid ? "settled" : "failed";
        await audit.LogAsync(
            action, nameof(StorePayment), existing.Id,
            $"Stripe payment of EUR {existing.AmountEur:0.00} {verb} on order {existing.OrderId} (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, nameof(StoreOrder));
    }

    /// <summary>
    /// Handles <c>checkout.session.expired</c>: defensively removes an orphan
    /// <see cref="StorePaymentStatus.Pending"/> row that somehow predates a missing <c>completed</c>
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
        if (existing is null || existing.Status != StorePaymentStatus.Pending)
            return;

        await repo.DeletePaymentAsync(existing.Id, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentExpired, nameof(StorePayment), existing.Id,
            $"Removed orphan pending Stripe payment of EUR {existing.AmountEur:0.00} on order {existing.OrderId} after session expiry (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, nameof(StoreOrder));
    }

    /// <summary>
    /// Phase 5 — not yet implemented. <b>Freeze seam (#816):</b> before flipping
    /// <see cref="StoreOrder.State"/> to <see cref="StoreOrderState.InvoiceIssued"/>, the
    /// implementation MUST re-write each line's <c>UnitPriceSnapshot</c>,
    /// <c>VatRateSnapshot</c>, and <c>DepositAmountSnapshot</c> from the current catalog
    /// price, so the issued invoice captures the exact prices shown at issue time. Until
    /// then every order stays Open and reprices live, which is the desired in-season behavior.
    /// </summary>
    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public async Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
    {
        var seasonsForYear = (await campService.GetCampsForYearAsync(year, ct))
            .SelectMany(camp => camp.Seasons.Where(season => season.Year == year))
            .ToDictionary(season => season.Id);
        var products = await repo.GetAllProductsForYearAsync(year, ct);

        var campOrders = seasonsForYear.Count == 0
            ? Array.Empty<StoreOrder>()
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
                StoreOrderCounterpartyType.Camp,
                sid,
                campName,
                null, // Label removed from the UI (#816); column retained, unused.
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
                StoreOrderCounterpartyType.Team,
                tid,
                teamName,
                null, // Label removed from the UI (#816); column retained, unused.
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
            .Select(p => new StoreCrossTabColumn(p.ProductId, p.ProductName, p.TotalQty))
            .OrderBy(c => c.ProductName, StringComparer.Ordinal)
            .ToList();

        var counterpartyRows = new List<StoreCrossTabRow>();
        foreach (var g in campOrdersInYear.GroupBy(o => o.CampSeasonId!.Value))
        {
            var perProduct = g
                .SelectMany(o => o.Lines)
                .GroupBy(l => l.ProductId)
                .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
            var total = perProduct.Values.Sum();
            counterpartyRows.Add(new StoreCrossTabRow(
                StoreOrderCounterpartyType.Camp,
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
            counterpartyRows.Add(new StoreCrossTabRow(
                StoreOrderCounterpartyType.Team,
                g.Key,
                teamNames.TryGetValue(g.Key, out var n) ? n : "(unknown team)",
                total,
                perProduct));
        }
        return new StoreSummaryDto(
            year,
            byCounterparty,
            byItem,
            new StoreCrossTabDto(productColumns, counterpartyRows));
    }

    private static ProductDto MapProduct(StoreProduct p) =>
        new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
            p.DepositAmountEur, p.OrderableUntil, p.IsActive);

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
        var activeEvent = await shifts.GetActiveAsync();
        var catalogYear = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
        var prices = new Dictionary<Guid, BalanceCalculator.ProductPrice>();
        foreach (var product in await repo.GetAllProductsForYearAsync(catalogYear, ct))
            prices[product.Id] = new BalanceCalculator.ProductPrice(
                product.UnitPriceEur, product.VatRatePercent, product.DepositAmountEur);
        return prices;
    }

    private async Task<OrderDto> MapOrderAsync(
        StoreOrder o,
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
            ? StoreOrderCounterpartyType.Team
            : StoreOrderCounterpartyType.Camp;

        var displayName = await ResolveCounterpartyDisplayNameAsync(o, ct);

        return new OrderDto(
            o.Id,
            o.CampSeasonId,
            o.TeamId,
            counterpartyType,
            displayName,
            o.Year,
            null, // Label removed from the UI (#816); column retained, unused.
            o.State,
            o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
            o.IssuedInvoiceId,
            lines,
            payments,
            balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
            balance.PaymentsTotalEur, balance.BalanceEur,
            o.CreatedAt);
    }

    private async Task<string> ResolveCounterpartyDisplayNameAsync(StoreOrder o, CancellationToken ct)
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

    private static void EnsureBillable(StoreOrder order)
    {
        if (order.TeamId is not null)
            throw new InvalidOperationException("Team orders are non-billable.");
    }
}
