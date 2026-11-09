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

namespace Humans.Store.Services;

internal sealed class Service(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampService campService,
    ITeamService teamService,
    IClock clock,
    ISettingsService settingsService,
    IStripeService stripeService,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IOptions<StoreSectionOptions> options,
    ILogger<Service> logger) : IApplicationService
{
    private readonly StoreOrderReader orderReader = new(repo, campService, teamService, clock, settingsService);
    private readonly StoreStripePayments stripePayments = new(repo, audit, stripeService, clock, logger);
    private readonly StoreCatalog productCatalog = new(repo, audit, clock, logger);

    public Task<IndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default) =>
        orderReader.GetIndexDataAsync(userId, ct);

    public Task<IndexData> GetAllCounterpartiesIndexDataAsync(CancellationToken ct = default) =>
        orderReader.GetAllCounterpartiesIndexDataAsync(ct);

    public Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default) =>
        productCatalog.GetActiveCatalogAsync(year, ct);

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
            canEdit,
            canPayAuthorized && order.BalanceEur > 0 && !hasPendingPayment && order.CounterpartyType == OrderCounterpartyType.Camp,
            stripeService.IsStoreCheckoutConfigured);
    }

    public Task<IReadOnlyList<ProductDto>> GetAllProductsForYearAsync(int year, CancellationToken ct = default) =>
        productCatalog.GetAllProductsForYearAsync(year, ct);

    public Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default) =>
        productCatalog.GetProductAsync(productId, ct);

    public Task<CatalogSaveResult> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default) =>
        productCatalog.CreateProductAsync(draft, actorUserId, ct);

    public Task<AdminMutationResult> UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default) =>
        productCatalog.UpdateProductAsync(draft, actorUserId, ct);

    public Task<CatalogSaveResult> SaveProductWithResultAsync(
        ProductSaveRequest request,
        Guid actorUserId,
        CancellationToken ct = default) =>
        productCatalog.SaveProductWithResultAsync(request, actorUserId, ct);

    public Task<AdminMutationResult> DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default) =>
        productCatalog.DeactivateProductAsync(productId, actorUserId, ct);

    public Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default) =>
        orderReader.GetOrderAsync(orderId, ct);

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

        var order = new Order
        {
            CampSeasonId = campSeasonId,
            TeamId = null,
            Year = season.Year
        };
        return await SaveNewOrderAsync(order, actorUserId,
            $"Created store order for camp season {campSeasonId}", ct);
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

        var currentPrices = await LoadCurrentPricesAsync(order.Year, ct);
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

        var order = new Order
        {
            CampSeasonId = null,
            TeamId = teamId,
            Year = year
        };
        return await SaveNewOrderAsync(order, actorUserId,
            $"Created store order for team '{team.Name}' ({year})", ct);
    }

    private async Task<MutationResult> SaveNewOrderAsync(
        Order order, Guid actorUserId, string description, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        order.Id = Guid.NewGuid();
        order.State = OrderState.Open;
        order.CreatedAt = now;
        order.UpdatedAt = now;
        await repo.AddOrderAsync(order, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreOrderCreated, AuditEntityTypes.Order, order.Id,
            description, actorUserId);
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
    public Task<CheckoutSessionResult> CreateStripeCheckoutSessionAsync(
        OrderDto order, decimal amountEur, string returnUrl, CancellationToken ct = default) =>
        stripePayments.CreateStripeCheckoutSessionAsync(order, amountEur, returnUrl, ct);

    public Task RecordStripePaymentAsync(
        Guid orderId,
        string paymentIntentId,
        decimal amountEur,
        PaymentStatus status = PaymentStatus.Paid,
        CancellationToken ct = default) =>
        stripePayments.RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, status, ct);

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
        if (amountEur > 9_999_999_999.99m || decimal.Round(amountEur, 2) != amountEur)
            return RefuseAdminMutation(orderId, actorUserId,
                "Amount must be at most 9,999,999,999.99 with no more than two decimal places.");
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
            var depositTotal = BalanceCalculator.Compute(order, await LoadCurrentPricesAsync(order.Year, ct)).DepositTotalEur;
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

    public Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default) =>
        stripePayments.GetStripeReconciliationAsync(orderReader, ct);

    public Task<StripeReconciliationResult> RecordMissingStripePaymentsAsync(
        Guid actorUserId, CancellationToken ct = default) =>
        stripePayments.RecordMissingStripePaymentsAsync(actorUserId, ct);

    public Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default) =>
        stripePayments.HandleStripeCheckoutWebhookEventAsync(evt, ct);

    /// <summary>Issues or recovers the billable order's invoice and freezes its prices.</summary>
    [ExternalWrite]
    public async Task<AdminMutationResult> IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null)
            return RefuseAdminMutation(orderId, actorUserId, "Order not found.");
        if (order.TeamId is not null)
            return RefuseAdminMutation(orderId, actorUserId, "Team orders are non-billable.");
        return await new StoreInvoiceIssuer(repo, audit, clock, holded, holdedWrite, options, orderReader, logger)
            .IssueAsync(order, actorUserId, ct);
    }

    private AdminMutationResult RefuseAdminMutation(Guid orderId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store admin mutation rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            orderId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    public Task<SummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default) =>
        orderReader.GetStoreSummaryAsync(year, ct);

    private Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        int orderYear, CancellationToken ct) => orderReader.LoadCurrentPricesAsync(orderYear, ct);
}
