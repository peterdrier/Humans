using Humans.Backdoor.Filters;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Store.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Backdoor.Controllers;

/// <summary>Any active key may read; there is no role scoping. No paging: a season is a few hundred rows.</summary>
[ApiController]
[Route("api/backdoor/store")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
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
                qty = l.Qty,
                unitPrice = l.UnitPrice,
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
}
