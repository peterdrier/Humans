using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Camps.Contracts;
using Humans.Settings.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using NodaTime.Text;
using Humans.Teams.Contracts;
using Humans.Store.Services.Dtos;
using Humans.Stripe.Contracts;
using NodaTime;
using System.Globalization;
using System.Resources;
using Humans.Base.Threading;

namespace Humans.Store.Services;

internal sealed class Service(
    IStoreRepository repo,
    IAuditLogService audit,
    IAuditViewerService auditViewer,
    ICampService campService,
    ITeamService teamService,
    IClock clock,
    ISettingsService settingsService,
    IStripeService stripeService,
    CatalogService catalogService,
    ILogger<Service> logger) : IStoreAccountingRead
{
    public async Task<OrderPageData> GetOrderPageDataAsync(
        OrderDto order,
        bool canEdit,
        bool canPayAuthorized,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProductDto> catalog = [];
        if (canEdit)
        {
            catalog = await catalogService.GetActiveCatalogAsync(order.Year, ct);
        }

        // A pending async payment is excluded from BalanceEur, so without the HasPendingSettlement guard the full
        // balance stays payable a second time while the mandate settles: a double charge.
        return new OrderPageData(
            order,
            catalog,
            canEdit,
            canPayAuthorized && order.BalanceEur > 0 && !order.HasPendingSettlement && order.CounterpartyType == OrderCounterpartyType.Camp,
            stripeService.IsStoreCheckoutConfigured);
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
            "Created store order", ct);
    }

    public async Task<MutationResult> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await teamService.GetTeamAsync(teamId, ct);
        if (team is null)
            return RefuseCreation("Store_TeamMissing", "team", teamId, actorUserId);
        if (team.ParentTeamId is not null)
            return RefuseCreation("Store_DepartmentOnly", "team", teamId, actorUserId);

        var year = GetCurrentEventYear();

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
        var today = clock.TodayIn(settingsService.GetActiveEventSettings()?.TimeZone);
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
            $"Added {qty} × '{product.Name}' at EUR {product.UnitPriceEur:0.00} + {product.VatRatePercent:0.##}% VAT"
                + (product.DepositAmountEur is { } deposit ? $" + EUR {deposit:0.00} deposit" : string.Empty)
                + (deadlinePassed ? $" (past order deadline {LocalDatePattern.Iso.Format(product.OrderableUntil)})" : string.Empty),
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
        var today = clock.TodayIn(settingsService.GetActiveEventSettings()?.TimeZone);
        var deadlinePassed = today > ctx.ProductOrderableUntil;

        await repo.RemoveLineAsync(lineId, ct);
        await audit.LogAsync(
            StoreAuditActions.StoreLineRemoved, AuditEntityTypes.OrderLine, lineId,
            $"Removed {ctx.Qty} × '{ctx.ProductName}' (added at EUR {ctx.UnitPriceSnapshot:0.00})"
                + (deadlinePassed ? $" (past order deadline {LocalDatePattern.Iso.Format(ctx.ProductOrderableUntil)})" : string.Empty),
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
            "Updated counterparty",
            actorUserId);
        return MutationResult.Success;
    }

    private MutationResult RefuseMutation(string errorKey, Guid orderId, Guid actorUserId, Guid? lineId = null)
    {
        logger.LogWarning("Store mutation rejected for order {OrderId}, line {LineId}, actor {ActorUserId}: {ErrorKey}",
            orderId, lineId, actorUserId, errorKey);
        return MutationResult.Failure(errorKey);
    }

    /// <summary>Where a FinanceAdmin issues a refund of the payment by hand.</summary>
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

    internal async Task<bool> ResolveLegacyOrderYearAsync(
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

    public async Task<AdminMutationResult> DeleteOrderAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        // Checkout creation and webhook recording must finish before deletion checks their retained state.
        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
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

        if (order.HasCheckoutAttempt)
            return RefuseAdminMutation(orderId, actorUserId,
                $"Order {orderId} has an unresolved Stripe checkout; wait for its completion or expiry before deleting it.");

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

    /// <summary>
    /// A <see cref="PaymentMethod.BankTransfer"/> books money received in the bank, citing the Holded bank movement id
    /// and its value date; a <see cref="PaymentMethod.DepositReturn"/> credits a returned deposit; a
    /// <see cref="PaymentMethod.Refund"/> books money sent back by hand from the Stripe dashboard, stored negative and
    /// citing its reference. Transfers and refunds are uncapped (a camp may have overpaid); deposit returns never
    /// exceed the order's deposits. A <paramref name="valueDate"/> becomes the payment's received date.
    /// </summary>
    public async Task<AdminMutationResult> RecordAdminPaymentAsync(
        Guid orderId,
        PaymentMethod method,
        decimal amountEur,
        string? externalRef,
        string? notes,
        LocalDate? valueDate,
        Guid actorUserId,
        CancellationToken ct = default,
        Guid? productId = null)
    {
        if (method is not (PaymentMethod.BankTransfer or PaymentMethod.DepositReturn or PaymentMethod.Refund
            or PaymentMethod.WriteOff))
            return RefuseAdminMutation(orderId, actorUserId,
                $"Only bank transfers, deposit returns, refunds and write-offs can be recorded by hand, not {method}.");
        if (amountEur <= 0)
            return RefuseAdminMutation(orderId, actorUserId, "Amount must be greater than zero.");
        if (amountEur > 9_999_999_999.99m || decimal.Round(amountEur, 2) != amountEur)
            return RefuseAdminMutation(orderId, actorUserId,
                "Amount must be at most 9,999,999,999.99 with no more than two decimal places.");
        if (method == PaymentMethod.Refund && string.IsNullOrWhiteSpace(externalRef))
            return RefuseAdminMutation(orderId, actorUserId, "A refund needs a reference (e.g. the Stripe refund id).");
        if (method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(externalRef))
            return RefuseAdminMutation(orderId, actorUserId, "A bank transfer needs a reference (the Holded bank movement id).");
        if (method == PaymentMethod.BankTransfer && valueDate is null)
            return RefuseAdminMutation(orderId, actorUserId, "A bank transfer needs its value date.");
        if (method == PaymentMethod.WriteOff && string.IsNullOrWhiteSpace(notes))
            return RefuseAdminMutation(orderId, actorUserId, "A write-off needs a reason.");

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
        if (method == PaymentMethod.DepositReturn && productId is null)
            return RefuseAdminMutation(orderId, actorUserId, "A deposit return needs the product whose deposit it returns.");

        var totals = BalanceCalculator.Compute(order, await LoadCurrentPricesAsync(order.Year, ct));
        if (method == PaymentMethod.DepositReturn)
        {
            var lineTotals = totals.Lines.ToDictionary(t => t.LineId);
            var depositTotal = order.Lines.Where(l => l.ProductId == productId).Sum(l => lineTotals[l.Id].DepositEur);
            var returns = order.Payments
                .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
                .ToList();
            // Returns recorded before they named a product returned every deposit on their order.
            var alreadyReturned = returns.Any(p => p.ProductId is null)
                ? depositTotal
                : returns.Where(p => p.ProductId == productId).Sum(p => p.AmountEur);
            var remaining = depositTotal - alreadyReturned;
            if (amountEur > remaining)
                return RefuseAdminMutation(orderId, actorUserId,
                    $"Deposit return of EUR {amountEur:0.00} exceeds the EUR {remaining:0.00} of this product's deposit "
                    + $"still held (EUR {depositTotal:0.00} deposited, EUR {alreadyReturned:0.00} already returned).");
        }
        if (method == PaymentMethod.WriteOff)
        {
            if (order.State != OrderState.Open)
                return RefuseAdminMutation(orderId, actorUserId,
                    "A write-off goes on the invoice as a discount, so it can only be recorded before invoicing.");
            if (totals.BalanceEur <= 0m)
                return RefuseAdminMutation(orderId, actorUserId, "The camp owes nothing, so there is no shortfall to write off.");
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
            ProductId = method == PaymentMethod.DepositReturn ? productId : null,
            // Midnight UTC falls on the same calendar day in UTC and in the org zone (Europe/Madrid), so the value
            // date reads back unchanged wherever ReceivedAt is shown.
            ReceivedAt = valueDate?.AtMidnight().InUtc().ToInstant() ?? clock.GetCurrentInstant(),
            RecordedByUserId = actorUserId,
        };
        await repo.AddPaymentAsync(payment, ct);
        await audit.LogAsync(
            StoreAuditActions.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
            (method == PaymentMethod.WriteOff
                ? $"Wrote off a shortfall of EUR {signed:0.00}: {notes}"
                : $"Recorded {MethodDescription(method)} of EUR {signed:0.00}")
                + (payment.ExternalRef is null ? string.Empty : $" (ref {payment.ExternalRef})")
                + (valueDate is { } d ? $", value date {LocalDatePattern.Iso.Format(d)}" : string.Empty),
            actorUserId, orderId, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

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
            $"Deleted {payment.Method} payment of EUR {payment.AmountEur:0.00} ({payment.Status}), "
                + $"received {payment.ReceivedAt}, ref {payment.ExternalRef ?? "none"}, PI {payment.StripePaymentIntentId ?? "none"}, "
                + $"recorded by {payment.RecordedByUserId?.ToString() ?? "none"}, notes {payment.Notes ?? "none"}",
            actorUserId, orderId, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

    private static string MethodDescription(PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => "bank transfer",
        PaymentMethod.DepositReturn => "deposit return",
        PaymentMethod.Refund => "refund",
        _ => method.ToString(),
    };

    private AdminMutationResult RefuseAdminMutation(Guid orderId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store admin mutation rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            orderId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    private static readonly ResourceManager Resources = new(typeof(StoreResource));

    public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var o = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (o is null) return null;
        var productIds = o.Lines.Select(l => l.ProductId).Distinct().ToList();
        var productNames = await LoadProductNamesAsync(productIds, ct);
        var currentPrices = await LoadCurrentPricesAsync(o.Year, ct);
        return await MapOrderAsync(o, productNames, currentPrices, ct);
    }

    public Task<IndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default) =>
        BuildIndexDataAsync(userId, allCounterparties: false, ct);

    public Task<IndexData> GetAllCounterpartiesIndexDataAsync(CancellationToken ct = default) =>
        BuildIndexDataAsync(userId: Guid.Empty, allCounterparties: true, ct);

    private async Task<IndexData> BuildIndexDataAsync(
        Guid userId,
        bool allCounterparties,
        CancellationToken ct)
    {
        var year = GetCurrentEventYear();
        var products = await repo.GetAllProductsForYearAsync(year, ct);
        var activeProducts = products.Where(product => product.IsActive).Select(CatalogService.MapProduct).ToList();
        var currentPrices = ToPrices(products);

        var counterparties = new List<CounterpartyOrders>();

        var campSeasons = (await campService.GetCampsForYearAsync(year, ct))
            .SelectMany(camp => camp.Seasons)
            .Where(season => season.Year == year && (allCounterparties || season.IsLead(userId)))
            .ToList();

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
            activeProducts,
            counterparties);
    }

    /// <summary>Falls back to the current UTC year before an active event exists.</summary>
    public int GetCurrentEventYear()
    {
        var activeEvent = settingsService.GetActiveEventSettings();
        return activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    }

    /// <summary>
    /// Includes deactivated products from the order's year. Legacy <c>Year = 0</c> rows retain the active-year
    /// catalog until their year is repaired by an authorized mutation.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice>> LoadCurrentPricesAsync(
        int orderYear, CancellationToken ct)
    {
        var catalogYear = orderYear > 0 ? orderYear : GetCurrentEventYear();
        return ToPrices(await repo.GetAllProductsForYearAsync(catalogYear, ct));
    }

    private static IReadOnlyDictionary<Guid, BalanceCalculator.ProductPrice> ToPrices(IEnumerable<Product> products) =>
        products.ToDictionary(
            product => product.Id,
            product => new BalanceCalculator.ProductPrice(
                product.UnitPriceEur, product.VatRatePercent, product.DepositAmountEur));

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
                p.Id, p.AmountEur, p.Method, p.Status, p.StripePaymentIntentId, p.ExternalRef, p.ReceivedAt, p.Notes, p.ProductId))
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
            o.CreatedAt)
        { HasCheckoutAttempt = o.HasCheckoutAttempt, TotalDueEur = balance.TotalDueEur };
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
        var currentPrices = ToPrices(products);

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
                g.Sum(x => (long)x.Qty),
                g.Sum(x => x.TotalEur)))
            .ToList();

        var productColumns = byItem
            .Select(p => new CrossTabColumn(p.ProductId, p.ProductName, p.TotalQty))
            .ToList();

        var counterpartyRows = counterparties
            .GroupBy(c => (c.Type, c.Id))
            .Select(g =>
            {
                var perProduct = g
                    .SelectMany(c => c.Order.Lines)
                    .GroupBy(l => l.ProductId)
                    .ToDictionary(lg => lg.Key, lg => lg.Sum(l => (long)l.Qty));
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
                    o.TeamId,
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
                    product?.HoldedExpenseAccountNum,
                    product?.HoldedTransferAccountNum,
                    l.Qty,
                    t.EffectiveUnitPrice,
                    l.UnitPriceSnapshot,
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

    /// <summary>Far above any real order's trail; a cap only so a runaway writer can't make the export unbounded.</summary>
    private const int HistoryLimit = 5000;

    /// <summary>
    /// The order page's admin history for every order of the year, one audit read per order and per product: the
    /// audit log is the history and Store keeps no copy.
    /// </summary>
    public async Task<IReadOnlyList<AccountingOrderHistoryDto>> GetOrderHistoryAsync(
        int year, Guid actorUserId, CancellationToken ct = default)
    {
        var (orders, context) = await LoadYearOrdersForAccountingAsync(year, ct);
        var orderActions = StoreAuditActions.OrderHistoryForAdmins.Select(a => a.Name).ToList();

        var priceChanges = new Dictionary<Guid, IReadOnlyList<AuditEvent>>();
        foreach (var productId in orders.SelectMany(o => o.Lines).Select(l => l.ProductId).Distinct())
            priceChanges[productId] = await auditViewer.GetFilteredAsync(
                AuditEntityTypes.Product, productId, userId: null,
                [StoreAuditActions.StoreProductPriceChanged.Name], HistoryLimit, ct: ct);

        var rows = new List<AccountingOrderHistoryDto>();
        foreach (var o in orders)
        {
            var (counterpartyType, label) = CounterpartyOf(o, context);
            var events = (await auditViewer.GetFilteredAsync(
                    entityType: null, o.Id, userId: null, orderActions, HistoryLimit, includeRelated: true, ct))
                .Concat(o.Lines.Select(l => l.ProductId).Distinct()
                    .SelectMany(id => priceChanges[id])
                    .Where(e => e.OccurredAt >= o.CreatedAt));
            rows.AddRange(events
                .OrderBy(e => e.OccurredAt)
                .Select(e => new AccountingOrderHistoryDto(
                    year, o.Id, counterpartyType, label, e.OccurredAt, e.Action.Name,
                    e.ActorUserId, e.ActorDisplayName, e.Description)));
        }

        await audit.LogAsync(
            StoreAuditActions.StoreOrderHistoryExported, AuditEntityTypes.Order, Guid.Empty,
            $"Store order history exported for {year}: {rows.Count} entries across {orders.Count} orders",
            actorUserId);
        return rows;
    }

    private static (OrderCounterpartyType Type, string Label) CounterpartyOf(Order o, YearContext context) =>
        o.TeamId is { } tid
            ? (OrderCounterpartyType.Team, context.TeamNames.GetValueOrDefault(tid, "(unknown team)"))
            : (OrderCounterpartyType.Camp,
                o.CampSeasonId is { } sid && context.SeasonsForYear.TryGetValue(sid, out var season)
                    ? season.Name
                    : "(unknown camp)");

    internal static readonly TrackedLock StripePaymentLock = new("Store.StripePayments");
    internal const decimal MaximumCheckoutAmountEur = 999_999.99m;

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
        else if (decimal.Round(amountEur, 2) != amountEur)
            errorKey = "Store_PaymentPrecision";
        else if (amountEur > order.BalanceEur)
        {
            errorKey = "Store_PaymentAboveBalance";
            maximumAmount = order.BalanceEur;
        }
        else if (amountEur > MaximumCheckoutAmountEur)
        {
            errorKey = "Store_PaymentAboveCheckoutLimit";
            maximumAmount = MaximumCheckoutAmountEur;
        }
        else if (order.HasPendingSettlement)
            errorKey = "Store_PaymentPending";

        if (errorKey is not null)
        {
            logger.LogWarning("Store checkout rejected for order {OrderId}: {ErrorKey}", order.Id, errorKey);
            return new CheckoutSessionResult(null, errorKey, maximumAmount);
        }

        var name = order.CounterpartyName ?? Resources.GetString("Store_CheckoutCampOrder", CultureInfo.CurrentUICulture)!;
        var description = $"Nobodies Collective - {name}";
        var sessionUrl = await OpenCheckoutSessionAsync(
            order.Id, amountEur, returnUrl, order.CounterpartyEmail, description, ct);
        if (sessionUrl is null)
            return new CheckoutSessionResult(null, "Store_PaymentPending");
        return new CheckoutSessionResult(sessionUrl, null);
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

            using (await StripePaymentLock.AcquireAsync(logger, ct))
            {
                await RecordStripePaymentCoreAsync(orderId, pi, amount, PaymentStatus.Paid, ct); // idempotent on PI id
                // Stands in for the undelivered completed webhook, so it releases the attempt the same way.
                await ClearCheckoutAttemptAsync(s, ct);
            }
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

    /// <summary>
    /// Books every Stripe refund on the order's paid Stripe payments that no <see cref="PaymentMethod.Refund"/> row cites
    /// yet, as a negative refund carrying the Stripe refund id: <see cref="PaymentStatus.Pending"/> while Stripe is still
    /// sending it, so the amount owed drops at once, and moves booked ones to Paid or Failed as Stripe settles them. Read only on the Stripe side: the refund itself
    /// is issued by a person on the Stripe dashboard. Running it again books nothing new.
    /// </summary>
    public async Task<StripeRefundCheckResult> RecordStripeRefundsAsync(
        Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null) return RefuseRefundCheck(orderId, "Order not found.");
        if (order.TeamId is not null) return RefuseRefundCheck(orderId, "Team orders are non-billable.");

        var paymentIntents = order.Payments
            .Where(p => p.Method == PaymentMethod.Stripe && p.Status == PaymentStatus.Paid && p.StripePaymentIntentId is not null)
            .Select(p => p.StripePaymentIntentId!)
            .ToList();
        if (paymentIntents.Count == 0)
            return RefuseRefundCheck(orderId, "This order has no paid Stripe payments to check.");

        var refunds = new List<StoreRefundData>();
        foreach (var pi in paymentIntents)
        {
            var listed = await stripeService.ListStoreRefundsAsync(pi, ct);
            if (listed is null)
                return RefuseRefundCheck(orderId,
                    "Couldn't read refunds from Stripe: the Store account's read key is missing or lacks refund read access.");
            refunds.AddRange(listed);
        }

        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
        // Reread under the lock so a concurrent check can't book the same refund twice.
        order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null) return RefuseRefundCheck(orderId, "Order not found.");
        var booked = order.Payments
            .Where(p => p.Method == PaymentMethod.Refund && p.ExternalRef is not null)
            .GroupBy(p => p.ExternalRef!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var recorded = 0;
        var total = 0m;
        var pending = 0;
        var settled = 0;
        var cancelled = 0;
        foreach (var refund in refunds.OrderBy(r => r.CreatedAt))
        {
            var status = RefundStatusOf(refund.Status);
            if (booked.TryGetValue(refund.RefundId, out var existing))
            {
                // Only forward moves: Pending → Paid/Failed, or a refund Stripe later reverses → Failed.
                if (existing.Status == status || existing.Status == PaymentStatus.Failed
                    || (existing.Status == PaymentStatus.Paid && status == PaymentStatus.Pending))
                    continue;
                await repo.UpdatePaymentStatusAsync(existing.Id, status, ct);
                var (action, verb) = status == PaymentStatus.Paid
                    ? (StoreAuditActions.StorePaymentSettled, "completed")
                    : (StoreAuditActions.StorePaymentFailed, $"{refund.Status} in Stripe, no longer counted");
                await audit.LogAsync(action, AuditEntityTypes.Payment, existing.Id,
                    $"Stripe refund {refund.RefundId} of EUR {existing.AmountEur:0.00} {verb}",
                    actorUserId, orderId, AuditEntityTypes.Order);
                if (status == PaymentStatus.Paid) settled++;
                else cancelled++;
                continue;
            }

            // A refund that never went through moves no money; nothing to book.
            if (status == PaymentStatus.Failed) continue;

            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                AmountEur = -refund.AmountEur,
                Method = PaymentMethod.Refund,
                MethodName = PaymentMethod.Refund,
                Status = status,
                ExternalRef = refund.RefundId,
                Notes = $"Stripe refund of {refund.PaymentIntentId}",
                ReceivedAt = refund.CreatedAt,
                RecordedByUserId = actorUserId,
            };
            await repo.AddPaymentAsync(payment, ct);
            await audit.LogAsync(
                StoreAuditActions.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
                $"Recorded {(status == PaymentStatus.Pending ? "pending " : "")}Stripe refund {refund.RefundId} of EUR {payment.AmountEur:0.00}",
                actorUserId, orderId, AuditEntityTypes.Order);
            recorded++;
            total += refund.AmountEur;
            if (status == PaymentStatus.Pending) pending++;
        }

        return new StripeRefundCheckResult(recorded, total, pending, settled, cancelled);
    }

    /// <summary>Stripe's <c>pending</c> and <c>requires_action</c> are money on its way out; <c>failed</c> and
    /// <c>canceled</c> are money that never left or came back.</summary>
    private static PaymentStatus RefundStatusOf(string stripeStatus) => stripeStatus switch
    {
        "succeeded" => PaymentStatus.Paid,
        "failed" or "canceled" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending,
    };

    public string DashboardUrl(string paymentIntentId) => stripeService.StorePaymentDashboardUrl(paymentIntentId);

    private StripeRefundCheckResult RefuseRefundCheck(Guid orderId, string reason)
    {
        logger.LogWarning("Stripe refund check refused for order {OrderId}: {Reason}", orderId, reason);
        return new StripeRefundCheckResult(0, 0m, 0, 0, 0, reason);
    }

    /// <summary>
    /// One payable session per order: an unchanged amount replays the open attempt's session through its idempotency key;
    /// a changed amount expires that session and opens a new attempt. Null when the superseded session had already been
    /// paid, so no replacement exists.
    /// </summary>
    public async Task<string?> OpenCheckoutSessionAsync(
        Guid orderId, decimal amountEur, string returnUrl, string? customerEmail, string description, CancellationToken ct)
    {
        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
        var order = await repo.GetOrderByIdAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Store order {orderId} not found.");

        var session = order is { CheckoutAttemptId: { } openAttempt } && order.CheckoutAmountEur == amountEur
            ? await TryReplayAsync(openAttempt)
            : null;
        if (IsComplete(session))
            return null;

        var attemptId = order.CheckoutAttemptId;
        if (session is not { Status: "open", Url: not null })
        {
            attemptId = Guid.NewGuid();
            session = await stripeService.CreateCheckoutSessionAsync(
                orderId, amountEur, returnUrl, returnUrl, customerEmail, description, ct,
                checkoutAttemptId: attemptId, supersededSessionId: order.CheckoutSessionId);
            if (IsComplete(session) || session.Url is null)
                return null;
        }

        if (attemptId != order.CheckoutAttemptId
            || !string.Equals(order.CheckoutSessionId, session.SessionId, StringComparison.Ordinal))
        {
            await WriteCheckoutAttemptAsync(orderId, attemptId, session.SessionId, amountEur, ct);
        }
        return session.Url;

        bool IsComplete(StoreCheckoutSessionData? s)
        {
            if (!string.Equals(s?.Status, "complete", StringComparison.Ordinal)) return false;
            logger.LogWarning("Checkout session {SessionId} for order {OrderId} already completed; no new session opened.",
                s!.SessionId, orderId);
            return true;
        }

        async Task<StoreCheckoutSessionData?> TryReplayAsync(Guid attemptId)
        {
            try
            {
                return await stripeService.CreateCheckoutSessionAsync(
                    orderId, amountEur, returnUrl, returnUrl, customerEmail, description, ct,
                    checkoutAttemptId: attemptId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stripe rejects a replay whose other arguments changed (a counterparty edit). Replacing the attempt
                // expires its session first, so falling through never leaves two payable.
                logger.LogWarning(ex, "Replaying checkout attempt {AttemptId} for order {OrderId} failed; replacing it.",
                    attemptId, orderId);
                return null;
            }
        }
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
            $"{settlement} of EUR {amountEur:0.00} (PI {paymentIntentId})",
            "StripeWebhook",
            orderId, AuditEntityTypes.Order);
    }

    public async Task HandleStripeCheckoutWebhookEventAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct = default)
    {
        using var mutation = await StripePaymentLock.AcquireAsync(logger, ct);
        switch (evt.Kind)
        {
            case StoreCheckoutEventKind.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompletedAsync(evt, ct);
                await ClearCheckoutAttemptAsync(evt.Session, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded:
                await TransitionAsyncPaymentAsync(evt, PaymentStatus.Paid, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
                await TransitionAsyncPaymentAsync(evt, PaymentStatus.Failed, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionExpired:
                await HandleCheckoutSessionExpiredAsync(evt, ct);
                await ClearCheckoutAttemptAsync(evt.Session, ct);
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

    /// <summary>Only the order's current attempt is cleared; a superseded session's event leaves its replacement open.</summary>
    private async Task ClearCheckoutAttemptAsync(StoreCheckoutSessionData? session, CancellationToken ct)
    {
        if (session is not { OrderId: { } orderId }) return;
        var order = await repo.GetOrderByIdAsync(orderId, ct);
        if (order is null || !string.Equals(order.CheckoutSessionId, session.SessionId, StringComparison.Ordinal))
            return;

        await WriteCheckoutAttemptAsync(orderId, null, null, null, ct);
    }

    /// <summary>
    /// Re-reads the row so only the checkout columns change: a full-row write from a read taken before a Stripe round
    /// trip would overwrite a concurrent order edit.
    /// </summary>
    private async Task WriteCheckoutAttemptAsync(
        Guid orderId, Guid? attemptId, string? sessionId, decimal? amountEur, CancellationToken ct)
    {
        var order = await repo.GetOrderByIdAsync(orderId, ct);
        if (order is null) return;
        order.CheckoutAttemptId = attemptId;
        order.CheckoutSessionId = sessionId;
        order.CheckoutAmountEur = amountEur;
        await repo.UpdateOrderAsync(order, ct);
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
            $"Stripe payment of EUR {existing.AmountEur:0.00} {verb} (PI {paymentIntentId})",
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
            $"Removed orphan pending Stripe payment of EUR {existing.AmountEur:0.00} after session expiry (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, AuditEntityTypes.Order);
    }

}
