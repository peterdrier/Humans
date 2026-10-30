using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Stripe.Contracts;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreStripePayments(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    ILogger logger) : IApplicationService
{
    private static readonly TrackedLock StripePaymentLock = new("Store.StripePayments");

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
            AuditAction.StorePaymentRecorded, AuditEntityTypes.Payment, payment.Id,
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
        var action = target == PaymentStatus.Paid ? AuditAction.StorePaymentSettled : AuditAction.StorePaymentFailed;
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
            AuditAction.StorePaymentExpired, AuditEntityTypes.Payment, existing.Id,
            $"Removed orphan pending Stripe payment of EUR {existing.AmountEur:0.00} on order {existing.OrderId} after session expiry (PI {paymentIntentId})",
            "StripeWebhook",
            existing.OrderId, AuditEntityTypes.Order);
    }
}
