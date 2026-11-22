using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Store.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Backdoor.Controllers;

/// <summary>Any active key may read lines and payments; order history needs a Store admin. No paging: a season is a few hundred rows.</summary>
[ApiController]
[Route("api/backdoor/store")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
[BackdoorScope(BackdoorScope.Staff)]
[AllowAnonymous]
internal sealed class BackdoorStoreController(IStoreAccountingRead store, IUserService users)
    : ApiControllerBase(users)
{
    /// <summary>One object per order line of <paramref name="year"/>, camp and team orders both.</summary>
    [HttpGet("order-lines")]
    public async Task<IActionResult> OrderLines([FromQuery] int? year, CancellationToken ct)
    {
        if (year is null) return BadRequest(new { error = "Missing 'year'." });

        var lines = await store.GetOrderLinesAsync(year.Value, ct);
        return Ok(lines
            .OrderBy(l => l.CounterpartyLabel, StringComparer.Ordinal)
            .ThenBy(l => l.OrderId)
            .ThenBy(l => l.AddedAt)
            .Select(l => new
            {
                year = l.Year,
                orderId = l.OrderId,
                counterpartyType = l.CounterpartyType.ToString(),
                counterpartyLabel = l.CounterpartyLabel,
                counterpartyName = l.CounterpartyName,
                counterpartyVatId = l.CounterpartyVatId,
                counterpartyCountryCode = l.CounterpartyCountryCode,
                orderState = l.OrderState.ToString(),
                issuedInvoiceNumber = l.IssuedInvoiceNumber,
                lineId = l.LineId,
                productId = l.ProductId,
                productName = l.ProductName,
                holdedRevenueAccountNum = l.HoldedRevenueAccountNum,
                holdedExpenseAccountNum = l.HoldedExpenseAccountNum,
                holdedTransferAccountNum = l.HoldedTransferAccountNum,
                qty = l.Qty,
                unitPrice = l.UnitPrice,
                snapshotUnitPrice = l.SnapshotUnitPrice,
                vatRatePercent = l.VatRatePercent,
                lineGross = l.LineGross,
                lineNet = l.LineNet,
                lineVat = l.LineVat,
                depositAmount = l.DepositAmount,
                addedAt = l.AddedAt.ToIso8601(),
            }));
    }

    /// <summary>One object per <c>Paid</c> payment of <paramref name="year"/>; refunds are negative.</summary>
    [HttpGet("payments")]
    public async Task<IActionResult> Payments([FromQuery] int? year, CancellationToken ct)
    {
        if (year is null) return BadRequest(new { error = "Missing 'year'." });

        var payments = await store.GetPaymentsAsync(year.Value, ct);
        return Ok(payments
            .OrderBy(p => p.ReceivedAt)
            .ThenBy(p => p.PaymentId)
            .Select(p => new
            {
                year = p.Year,
                orderId = p.OrderId,
                counterpartyType = p.CounterpartyType.ToString(),
                counterpartyLabel = p.CounterpartyLabel,
                paymentId = p.PaymentId,
                amountEur = p.AmountEur,
                method = p.Method,
                stripePaymentIntentId = p.StripePaymentIntentId,
                externalRef = p.ExternalRef,
                receivedAt = p.ReceivedAt.ToIso8601(),
            }));
    }

    /// <summary>
    /// One object per audit entry about an order of <paramref name="year"/>: lines added and removed, payments, refunds,
    /// deposit returns, invoice issuance and catalog price changes to its products. Oldest first within an order. It carries
    /// the admin-only entries the order page shows Store admins, so the key's holder must be one.
    /// </summary>
    [HttpGet("order-history")]
    public async Task<IActionResult> OrderHistory([FromQuery] int? year, CancellationToken ct)
    {
        if (!StoreRoles.CanAdministerStore(User)) return Forbid();
        if (year is null) return BadRequest(new { error = "Missing 'year'." });

        var history = await store.GetOrderHistoryAsync(year.Value, GetCurrentUserId()!.Value, ct);
        return Ok(history
            .OrderBy(h => h.CounterpartyLabel, StringComparer.Ordinal)
            .ThenBy(h => h.OrderId)
            .ThenBy(h => h.OccurredAt)
            .Select(h => new
            {
                year = h.Year,
                orderId = h.OrderId,
                counterpartyType = h.CounterpartyType.ToString(),
                counterpartyLabel = h.CounterpartyLabel,
                occurredAt = h.OccurredAt.ToIso8601(),
                action = h.Action,
                actorUserId = h.ActorUserId,
                actorName = h.ActorDisplayName,
                description = h.Description,
            }));
    }
}
