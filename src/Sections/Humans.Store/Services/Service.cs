using Humans.Base.Extensions;
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
    private readonly StoreOrderAdministration orderAdministration = new(repo, audit, clock, logger);
    private readonly StoreOrderReader orderReader = new(repo, campService, teamService, clock, settingsService);
    private readonly StoreStripePayments stripePayments = new(repo, audit, stripeService, clock, logger);
    private readonly StoreMetadataRepair metadataRepair = new(repo, audit, campService, clock, logger);
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

    public Task<OrderYearRepairReport> GetOrderYearRepairReportAsync(CancellationToken ct = default) =>
        metadataRepair.GetOrderYearRepairReportAsync(ct);

    public Task<int> RepairOrderYearsAsync(Guid actorUserId, CancellationToken ct = default) =>
        metadataRepair.RepairOrderYearsAsync(actorUserId, ct);

    public Task<IReadOnlyList<PaymentMethodRepairRow>> GetPaymentMethodRepairRowsAsync(CancellationToken ct = default) =>
        metadataRepair.GetPaymentMethodRepairRowsAsync(ct);

    public Task<int> RepairPaymentMethodNamesAsync(Guid actorUserId, CancellationToken ct = default) =>
        metadataRepair.RepairPaymentMethodNamesAsync(actorUserId, ct);

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

    public Task<AdminMutationResult> DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default) =>
        orderAdministration.DeleteOrderAsync(orderId, actorUserId, orderReader, ct);

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

        await metadataRepair.ResolveLegacyOrderYearAsync(order, actorUserId, nameof(AddLineAsync), ct);
        if (order.Year == 0)
            return RefuseMutation("Store_OrderYearUnresolved", orderId, actorUserId);

        var product = await repo.GetProductByIdAsync(productId, ct);
        if (product is null) return RefuseMutation("Store_ProductMissing", orderId, actorUserId);

        if (!product.IsActive)
            return RefuseMutation("Store_ProductInactive", orderId, actorUserId);

        if (product.Year != order.Year)
            return RefuseMutation("Store_ProductYearMismatch", orderId, actorUserId);

        // OrderableUntil is enforced by OrderAuthorizationHandler; the auth-free service only annotates the audit entry.
        var today = clock.TodayIn((await settingsService.GetActiveEventSettingsAsync(ct))?.TimeZone);
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
        var today = clock.TodayIn((await settingsService.GetActiveEventSettingsAsync(ct))?.TimeZone);
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
    public Task<AdminMutationResult> RecordAdminPaymentAsync(
        Guid orderId,
        PaymentMethod method,
        decimal amountEur,
        string? externalRef,
        string? notes,
        Guid actorUserId,
        CancellationToken ct = default) =>
        orderAdministration.RecordAdminPaymentAsync(orderId, method, amountEur, externalRef, notes, actorUserId, orderReader, ct);

    /// <summary>
    /// Hard delete of a payment recorded in error. The row is gone for good, so the audit entry carries everything
    /// needed to reconstruct it.
    /// </summary>
    public Task<AdminMutationResult> DeletePaymentAsync(
        Guid orderId, Guid paymentId, Guid actorUserId, CancellationToken ct = default) =>
        orderAdministration.DeletePaymentAsync(orderId, paymentId, actorUserId, ct);

    public Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default) =>
        stripePayments.GetStripeReconciliationAsync(orderReader, ct);

    public Task<StripeReconciliationResult> RecordMissingStripePaymentsAsync(
        Guid actorUserId, CancellationToken ct = default) =>
        stripePayments.RecordMissingStripePaymentsAsync(actorUserId, ct);

    public Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default) =>
        stripePayments.HandleStripeCheckoutWebhookEventAsync(evt, ct);

    /// <summary>Issues or recovers the billable order's invoice and freezes its prices.</summary>
    [ExternalWrite]
    public Task<AdminMutationResult> IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default) =>
        new StoreInvoiceIssuer(repo, audit, clock, holded, holdedWrite, options, orderReader, logger)
            .IssueAsync(orderId, actorUserId, ct);

    public Task<SummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default) =>
        orderReader.GetStoreSummaryAsync(year, ct);
}
