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
                orders = [await MapOrderAsync(existing, productNames, ct)];
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

        return new StoreOrderPageData(
            order,
            catalog,
            order.CounterpartyDisplayName,
            canEdit,
            canPayAuthorized && order.BalanceEur > 0 && order.CounterpartyType == StoreOrderCounterpartyType.Camp,
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
        var result = new List<OrderDto>(orders.Count);
        foreach (var o in orders)
            result.Add(await MapOrderAsync(o, productNames, ct));
        return result;
    }

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await repo.GetProductNamesByIdsAsync(productIds, ct);
        return await MapOrderAsync(o, productNames, ct);
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
            Label = label,
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

        var balance = BalanceCalculator.Compute(order).BalanceEur;
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
        return await MapOrderAsync(order, productNames, ct);
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

        var today = await TodayInEventZoneAsync();
        if (today > product.OrderableUntil)
            throw new InvalidOperationException(
                $"Product '{product.Name}' order deadline ({product.OrderableUntil}) has passed");

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
            $"Added {qty} × '{product.Name}' to order {order.Id}",
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

        var today = await TodayInEventZoneAsync();
        if (today > ctx.ProductOrderableUntil)
            throw new InvalidOperationException(
                $"Line's product order deadline ({ctx.ProductOrderableUntil}) has passed");

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            AuditAction.StoreLineRemoved, nameof(StoreOrderLine), lineId,
            $"Removed line {lineId} from order {ctx.OrderId}",
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

    public async Task RecordStripePaymentAsync(Guid orderId, string paymentIntentId, decimal amountEur, CancellationToken ct = default)
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
            StripePaymentIntentId = paymentIntentId,
            ReceivedAt = clock.GetCurrentInstant(),
            RecordedByUserId = null,
        };
        await repo.AddPaymentAsync(payment, ct);
        await audit.LogAsync(
            AuditAction.StorePaymentRecorded, nameof(StorePayment), payment.Id,
            $"Recorded Stripe payment of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, nameof(StoreOrder));
    }

    public async Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default)
    {
        switch (evt.Kind)
        {
            case StoreCheckoutEventKind.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompletedAsync(evt, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded:
            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
            case StoreCheckoutEventKind.CheckoutSessionExpired:
                logger.LogWarning(
                    "Stripe webhook event {Kind} (id={EventId}) received but not yet handled - async-payment state machine pending (nobodies-collective/Humans#638).",
                    evt.Kind, evt.EventId);
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

        try
        {
            await RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, ct);
            logger.LogInformation(
                "Recorded Stripe payment for order {OrderId} (session {SessionId}, PI {PaymentIntentId}, EUR {Amount})",
                orderId, session.SessionId, paymentIntentId, amountEur);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to record Stripe payment for order {OrderId} (session {SessionId})",
                orderId, session.SessionId);
        }
    }

    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Phase 5");

    public async Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
    {
        var seasonsForYear = (await campService.GetCampsForYearAsync(year, ct))
            .SelectMany(camp => camp.Seasons.Where(season => season.Year == year))
            .ToDictionary(season => season.Id);
        var products = await repo.GetAllProductsForYearAsync(year, ct);

        var campOrders = seasonsForYear.Count == 0
            ? (IReadOnlyList<StoreOrder>)Array.Empty<StoreOrder>()
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

        var byCounterparty = new List<OrderSummaryDto>();

        foreach (var o in campOrdersInYear)
        {
            var totals = BalanceCalculator.Compute(o);
            var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
            var sid = o.CampSeasonId!.Value;
            var campName = seasonsForYear[sid].Name;
            byCounterparty.Add(new OrderSummaryDto(
                o.Id,
                StoreOrderCounterpartyType.Camp,
                sid,
                campName,
                o.Label,
                o.State,
                totalDue,
                totals.PaymentsTotalEur,
                totals.BalanceEur));
        }
        foreach (var o in teamOrders)
        {
            var totals = BalanceCalculator.Compute(o);
            var tid = o.TeamId!.Value;
            var teamName = teamNames.TryGetValue(tid, out var n) ? n : "(unknown team)";
            byCounterparty.Add(new OrderSummaryDto(
                o.Id,
                StoreOrderCounterpartyType.Team,
                tid,
                teamName,
                o.Label,
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
            .SelectMany(o => o.Lines.Select(l => new
            {
                l.ProductId,
                l.Qty,
                Subtotal = l.Qty * l.UnitPriceSnapshot,
                Vat = Math.Round(l.Qty * l.UnitPriceSnapshot * l.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero),
                Deposit = l.DepositAmountSnapshot is { } d ? l.Qty * d : 0m
            }))
            .ToList();

        var byItem = allLineProjections
            .GroupBy(x => x.ProductId)
            .Select(g => new ProductAggregateDto(
                g.Key,
                productNames.TryGetValue(g.Key, out var n) ? n : "(unknown)",
                g.Sum(x => x.Qty),
                g.Sum(x => x.Subtotal + x.Vat + x.Deposit)))
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

    private async Task<OrderDto> MapOrderAsync(
        StoreOrder o,
        IReadOnlyDictionary<Guid, string> productNames,
        CancellationToken ct)
    {
        var balance = BalanceCalculator.Compute(o);
        var totalsByLine = balance.Lines.ToDictionary(t => t.LineId);
        var lines = o.Lines.Select(l =>
        {
            var t = totalsByLine[l.Id];
            return new OrderLineDto(
                l.Id, l.OrderId, l.ProductId,
                productNames.GetValueOrDefault(l.ProductId, "(unknown product)"),
                l.Qty, l.UnitPriceSnapshot, l.VatRateSnapshot, l.DepositAmountSnapshot, l.AddedAt,
                t.SubtotalEur, t.VatEur, t.DepositEur, t.TotalEur);
        }).ToList();

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
            o.Label,
            o.State,
            o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
            o.IssuedInvoiceId,
            lines,
            balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
            balance.PaymentsTotalEur, balance.BalanceEur);
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
