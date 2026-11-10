using System.Globalization;
using System.Resources;
using Humans.Base.Attributes;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using Humans.Stripe.Contracts;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreStripePayments(
    IStoreRepository repo,
    IAuditLogService audit,
    IStripeService stripeService,
    IClock clock,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager Resources = new(typeof(StoreResource));

    internal static readonly TrackedLock StripePaymentLock = new("Store.StripePayments");

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
        else if (order.Payments.Any(p => p.Status == PaymentStatus.Pending))
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

    public async Task<StripeReconciliationReport> GetStripeReconciliationAsync(
        StoreOrderReader orderReader, CancellationToken ct = default)
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
            var order = await orderReader.GetOrderAsync(id, ct);
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
            $"{settlement} of EUR {amountEur:0.00} on order {orderId} (PI {paymentIntentId})",
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
}
