using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Store.Services.Dtos;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreOrderAdministration(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    ILogger<Service> logger) : IApplicationService
{
    public async Task<AdminMutationResult> DeleteOrderAsync(Guid orderId, Guid actorUserId, StoreOrderReader orderReader, CancellationToken ct = default)
    {
        // Checkout creation and webhook recording must finish before deletion checks their retained state.
        using var mutation = await StoreStripePayments.StripePaymentLock.AcquireAsync(logger, ct);
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

        var currentPrices = await orderReader.LoadCurrentPricesAsync(order.Year, ct);
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

    public async Task<AdminMutationResult> RecordAdminPaymentAsync(
        Guid orderId,
        PaymentMethod method,
        decimal amountEur,
        string? externalRef,
        string? notes,
        Guid actorUserId,
        StoreOrderReader orderReader, CancellationToken ct = default)
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
            var depositTotal = BalanceCalculator.Compute(order, await orderReader.LoadCurrentPricesAsync(order.Year, ct)).DepositTotalEur;
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

    private AdminMutationResult RefuseAdminMutation(Guid orderId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store admin mutation rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            orderId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }
}
