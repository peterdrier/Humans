using System.Globalization;
using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Store.Services;

internal sealed class StoreInvoiceIssuer(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IOptions<StoreSectionOptions> options,
    StoreOrderReader orderReader,
    ILogger logger) : IApplicationService
{
    /// <summary>
    /// Freezes the line snapshots at today's catalog price, then creates and <b>approves</b> the Holded document (a draft
    /// books nothing) and flips the order to <see cref="OrderState.InvoiceIssued"/> in one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full factura when counterparty details are on file, else a factura simplificada. Above
    /// <see cref="StoreSectionOptions.SimplifiedInvoiceThresholdEur"/> a receipt isn't legal, so a counterparty-less
    /// order that large is refused rather than downgraded.
    /// </para>
    /// <para>
    /// Idempotent on both sides: a document approved by an attempt that then failed locally leaves no trace here, so
    /// Holded is searched for one tagged with this order first. A match is adopted, never re-issued: a second approved
    /// factura is a real legal document, correctable only by a factura rectificativa.
    /// </para>
    /// </remarks>
    [ExternalWrite]
    public async Task<AdminMutationResult> IssueAsync(Order order, Guid actorUserId, CancellationToken ct = default)
    {
        // Before any Holded call, so a double-submit can't create a second document.
        if (order.IssuedInvoiceId is not null || order.State != OrderState.Open)
            return Refuse(order, actorUserId, "This order has already been invoiced.");

        if (order.Lines.Count == 0)
            return Refuse(order, actorUserId, "Cannot invoice an order with no lines.");

        if (!holded.IsConfigured)
            return Refuse(order, actorUserId,
                "Holded is not configured in this environment, so no invoice can be issued.");

        // Freeze seam: rewrite each snapshot from the live catalog so the document and the order agree forever after.
        var totals = BalanceCalculator.Compute(order, await orderReader.LoadCurrentPricesAsync(ct));
        var balanceRefusal = totals.BalanceEur == 0m ? null
            : $"An invoice can only be issued when the order balance is zero (currently EUR {totals.BalanceEur:0.00}).";
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            line.UnitPriceSnapshot = t.EffectiveUnitPrice;
            line.VatRateSnapshot = t.EffectiveVatRate;
            line.DepositAmountSnapshot = t.EffectiveDeposit;
        }

        // Recovery precedes the balance and line-account gates: payments or catalog assignments can change after Holded
        // approves a document whose local save failed.
        var tag = OrderDocumentTag(order.Id);
        if (await FindAlreadyIssuedDocumentAsync(tag, ct) is { } alreadyIssued)
        {
            var (adoptedKind, adoptedDoc) = alreadyIssued;
            // The order stayed Open, so it may have been edited or repriced since that document was created. Divergence
            // is a human's problem, not something to reconcile silently.
            if ((GetDocumentTotalsRefusal(order, adoptedDoc, totals)
                 ?? GetRecoveredLinesRefusal(order, adoptedDoc, totals)) is { } recoveryRefusal)
                return Refuse(order, actorUserId, recoveryRefusal);
            if (adoptedDoc.IsDraft == true)
            {
                if (balanceRefusal is not null) return Refuse(order, actorUserId, balanceRefusal);

                // Created but not approved. A draft books no revenue and carries no number, so freezing against it
                // leaves the order invoiced with no legal invoice: finish the issuance instead.
                adoptedDoc = await holdedWrite.ApproveSalesDocumentAsync(
                    adoptedKind, adoptedDoc.Id, CancellationToken.None);
            }
            return await CommitInvoiceAsync(
                order, adoptedDoc, totals,
                JsonSerializer.Serialize(new { kind = adoptedKind.ToString(), adopted = true, tag }),
                actorUserId,
                $"Adopted Holded {DocumentKindName(adoptedKind)} {adoptedDoc.DocNumber} "
                + $"(EUR {adoptedDoc.Total:0.00}), already issued for order {order.Id} by an earlier "
                + "attempt that failed before saving — no second document was created",
                ct);
        }

        if (balanceRefusal is not null) return Refuse(order, actorUserId, balanceRefusal);

        var totalDue = totals.TotalDueEur;
        if (totalDue <= 0m)
            return Refuse(order, actorUserId, "Cannot invoice an order with a zero total.");

        var products = (await repo.GetProductsByIdsAsync(
                order.Lines.Select(l => l.ProductId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);

        var (documentLines, lineRefusal) = await BuildInvoiceLinesAsync(order, totalsByLine, products, ct);
        if (lineRefusal is not null) return Refuse(order, actorUserId, lineRefusal);

        // A receipt carries no counterparty, so it's only lawful below the simplified-invoice threshold.
        var counterparty = ResolveInvoiceCounterparty(order);
        var kind = counterparty is null
            ? HoldedSalesDocumentKind.SalesReceipt
            : HoldedSalesDocumentKind.Invoice;
        if (counterparty is null && totalDue > options.Value.SimplifiedInvoiceThresholdEur)
            return Refuse(order, actorUserId,
                $"An order of EUR {totalDue:0.00} needs a full factura: fill in the counterparty's "
                + "name, address and tax id (or passport number) first.");

        var displayName = await orderReader.ResolveCounterpartyDisplayNameAsync(order, ct);
        string? contactId = null;
        if (counterparty is not null)
        {
            // Not the request token: everything from here writes to Holded, and a half-created contact or document has
            // no local compensation.
            contactId = await holdedWrite.CreateContactAsync(
                new HoldedContactInput
                {
                    Name = counterparty.Name,
                    Type = "client",
                    TaxCode = counterparty.TaxId,
                    Address = counterparty.Address,
                    CountryCode = counterparty.CountryCode,
                    Email = counterparty.Email,
                },
                CancellationToken.None);
        }

        var input = new HoldedSalesDocumentInput
        {
            ContactId = contactId,
            ContactName = counterparty?.Name ?? displayName,
            Date = clock.GetCurrentInstant(),
            Description = $"Nobodies Collective store — {displayName}",
            Notes = $"Humans store order {order.Id}",
            Tags = [tag],
            Lines = documentLines,
        };

        // A draft books no revenue, so issuing approves it.
        var document = await holdedWrite.IssueSalesDocumentAsync(kind, input, CancellationToken.None);

        return await CommitInvoiceAsync(
            order, document, totals,
            JsonSerializer.Serialize(new { kind = kind.ToString(), input }),
            actorUserId,
            $"Issued Holded {DocumentKindName(kind)} {document.DocNumber} "
            + $"for EUR {totalDue:0.00} on order {order.Id}",
            ct);
    }

    private AdminMutationResult Refuse(Order order, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store invoice rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            order.Id, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    /// <summary>
    /// Freezing against different totals would leave the order contradicting its legal invoice. A retry's lines and
    /// prices may also have changed since creation. Correcting an issued document is a factura rectificativa, a human
    /// decision, so both new issuance and recovery fail loudly.
    /// </summary>
    private static string? GetDocumentTotalsRefusal(
        Order order, HoldedSalesDocumentDto document, BalanceCalculator.Result totals)
    {
        // Deposits are ordinary tax-0 lines on the document, so they land in Holded's subtotal.
        var expectedSubtotal = decimal.Round(totals.LinesSubtotalEur + totals.DepositTotalEur, 2);
        var expectedTax = decimal.Round(totals.VatTotalEur, 2);
        var expectedTotal = decimal.Round(expectedSubtotal + expectedTax, 2);
        if (decimal.Round(document.Subtotal, 2) == expectedSubtotal
            && decimal.Round(document.Tax, 2) == expectedTax
            && decimal.Round(document.Total, 2) == expectedTotal)
            return null;

        return $"Holded already holds document {DocumentName(document)} for order {order.Id}, but it no longer matches "
            + $"the order: the document reads EUR {document.Subtotal:0.00} + {document.Tax:0.00} VAT "
            + $"= {document.Total:0.00}, while the order now totals EUR {expectedSubtotal:0.00} + "
            + $"{expectedTax:0.00} VAT = {expectedTotal:0.00}. Resolve it by hand — correcting an issued document is a factura "
            + "rectificativa, and no second document will be issued in the meantime.";
    }

    /// <summary>
    /// Two lines can trade amounts with every total unchanged, so a recovered document's line set (quantity, unit price,
    /// VAT rate) must match too; names are not compared, since a renamed product is the same sale. Recovery only, before
    /// any approval: the line read-back is unverified against a live v2 document, and once this attempt has approved a
    /// document a wrong refusal would leave a legal factura behind an order that can never freeze.
    /// </summary>
    private static string? GetRecoveredLinesRefusal(
        Order order, HoldedSalesDocumentDto document, BalanceCalculator.Result totals)
    {
        if (ExpectedLineSet(order, totals).SequenceEqual(
                LineSet(document.Lines.Select(l => new SalesLine(l.Units, l.Price, l.TaxPercent)))))
            return null;

        return $"Holded already holds document {DocumentName(document)} for order {order.Id}, but its lines "
            + (document.Lines.Count == 0 ? "could not be read" : "no longer match the order's quantities, unit prices or VAT rates")
            + ", though the totals agree. Resolve it by hand — correcting an issued document is a factura rectificativa, "
            + "and no second document will be issued in the meantime.";
    }

    private static string DocumentName(HoldedSalesDocumentDto document) =>
        string.IsNullOrEmpty(document.DocNumber) ? document.Id : document.DocNumber;

    /// <summary>The lines <see cref="BuildInvoiceLinesAsync"/> sends: each order line, plus a tax-0 deposit line when
    /// it carries a deposit.</summary>
    private static IReadOnlyList<SalesLine> ExpectedLineSet(Order order, BalanceCalculator.Result totals)
    {
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        var lines = new List<SalesLine>();
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            lines.Add(new SalesLine(line.Qty, t.EffectiveUnitPrice, t.EffectiveVatRate));
            if (t.EffectiveDeposit is { } deposit && deposit > 0m)
                lines.Add(new SalesLine(line.Qty, deposit, 0m));
        }
        return LineSet(lines);
    }

    private static IReadOnlyList<SalesLine> LineSet(IEnumerable<SalesLine> lines) =>
        lines.Select(l => l with { Price = decimal.Round(l.Price, 2) })
            .OrderBy(l => l.Units).ThenBy(l => l.Price).ThenBy(l => l.TaxPercent)
            .ToList();

    private sealed record SalesLine(decimal Units, decimal Price, decimal TaxPercent);

    /// <summary>Holded's list endpoints return <c>tags</c> but not <c>notes</c>, so the tag is what makes a document findable by its order.</summary>
    private static string OrderDocumentTag(Guid orderId) => $"humans-order-{orderId}";

    private static string DocumentKindName(HoldedSalesDocumentKind kind) =>
        kind == HoldedSalesDocumentKind.Invoice ? "factura" : "factura simplificada";

    /// <summary>
    /// Both kinds are searched: counterparty details can be filled in between attempts, so the earlier document may be of
    /// the other kind, and adopting it is still right. An approved match wins over a draft: approving the draft too would
    /// book the revenue twice under a second legal number.
    /// </summary>
    private async Task<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)?>
        FindAlreadyIssuedDocumentAsync(string tag, CancellationToken ct)
    {
        var found = await holdedWrite.FindSalesDocumentsByTagAsync(tag, ct);
        if (found.Count == 0) return null;
        if (found.Count > 1)
            // The duplicate this guard prevents already happened; only a factura rectificativa clears it. Adopting one
            // still stops a third.
            logger.LogWarning(
                "Holded holds {Count} documents tagged {Tag}: {Ids}. Adopting an approved one if there is any.",
                found.Count, tag, string.Join(", ", found.Select(f => $"{f.Kind}:{f.Document.Id}")));

        var approved = found.FirstOrDefault(f => f.Document.IsDraft != true);
        return approved.Document is not null ? approved : found[0];
    }

    /// <summary>Shared by first issuance and adoption, which differ only in the request payload they can honestly record and the audit line.</summary>
    private async Task<AdminMutationResult> CommitInvoiceAsync(
        Order order,
        HoldedSalesDocumentDto document,
        BalanceCalculator.Result totals,
        string requestPayload,
        Guid actorUserId,
        string auditDetail,
        CancellationToken ct)
    {
        if (GetDocumentTotalsRefusal(order, document, totals) is { } totalsRefusal)
            return Refuse(order, actorUserId, totalsRefusal);
        if (document.IsDraft == true || string.IsNullOrWhiteSpace(document.DocNumber))
            return Refuse(order, actorUserId,
                $"Holded document {document.Id} is still a draft or has no issued document number. "
                + "The order remains open. Retry after Holded confirms issuance; the existing document will be recovered.");

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            HoldedDocId = document.Id,
            HoldedDocNumber = document.DocNumber,
            IssuedAt = clock.GetCurrentInstant(),
            IssuedByUserId = actorUserId,
            RequestPayload = requestPayload,
            ResponsePayload = document.RawJson,
        };

        order.State = OrderState.InvoiceIssued;
        order.IssuedInvoiceId = invoice.Id;
        order.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveIssuedInvoiceAsync(invoice, order, ct);

        await audit.LogAsync(
            StoreAuditActions.StoreInvoiceIssued, AuditEntityTypes.Invoice, invoice.Id,
            auditDetail, actorUserId,
            order.Id, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

    /// <summary>
    /// Refundable deposits are a liability, not income, so each gets its own tax-0 line to the fianzas account instead of
    /// the item's revenue account.
    /// </summary>
    private async Task<(IReadOnlyList<HoldedSalesDocumentLineInput> Lines, string? Refusal)> BuildInvoiceLinesAsync(
        Order order,
        IReadOnlyDictionary<Guid, BalanceCalculator.LineTotals> totalsByLine,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken ct)
    {
        var missingAccount = order.Lines
            .Select(l => products.GetValueOrDefault(l.ProductId))
            .Where(p => p?.HoldedRevenueAccountNum is null)
            .Select(p => p?.Name ?? "(unknown product)")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missingAccount.Count > 0)
            return ([],
                "These catalog items have no Holded revenue account yet: "
                + string.Join(", ", missingAccount)
                + ". Set it on /Store/Admin/Catalog before issuing.");

        var needsDepositAccount = order.Lines.Any(l => totalsByLine[l.Id].DepositEur > 0m);
        var depositAccountNum = options.Value.DepositLiabilityAccountNum;
        if (needsDepositAccount && depositAccountNum is null)
            return ([],
                "This order carries refundable deposits but no deposit liability account is "
                + "configured (Store:DepositLiabilityAccountNum). Deposits are not income and "
                + "must not be booked to a revenue account.");

        // Holded resolves each number to its chart id when issuing; syncing the chart first picks up an account Acountax
        // created minutes ago.
        var wanted = order.Lines
            .Select(l => products[l.ProductId].HoldedRevenueAccountNum!.Value)
            .Concat(needsDepositAccount ? [depositAccountNum!.Value] : Array.Empty<int>())
            .ToHashSet();
        await holded.SyncChartAsync(ct);
        var chart = (await holded.GetChartAsync(ct)).Select(a => a.Number).ToHashSet();

        var unknown = wanted.Where(n => !chart.Contains(n)).ToList();
        if (unknown.Count > 0)
            return ([],
                "These accounts do not exist in Holded's chart of accounts: "
                + string.Join(", ", unknown.OrderBy(n => n))
                + ". Ask Acountax to create them, then re-issue.");

        var lines = new List<HoldedSalesDocumentLineInput>();
        foreach (var line in order.Lines)
        {
            var product = products[line.ProductId];
            var t = totalsByLine[line.Id];
            lines.Add(new HoldedSalesDocumentLineInput
            {
                Name = product.Name,
                Units = line.Qty,
                Price = t.EffectiveUnitPrice,
                Taxes = [SalesTaxKey(t.EffectiveVatRate)],
                AccountNum = product.HoldedRevenueAccountNum!.Value,
            });

            if (t.EffectiveDeposit is not { } depositPerUnit || depositPerUnit <= 0m) continue;
            lines.Add(new HoldedSalesDocumentLineInput
            {
                Name = $"{product.Name} — refundable deposit",
                Units = line.Qty,
                Price = depositPerUnit,
                Taxes = [SalesTaxKey(0m)],
                AccountNum = depositAccountNum!.Value,
            });
        }
        return (lines, null);
    }

    /// <summary>21 → <c>s_iva_21</c>, 7.5 → <c>s_iva_75</c>: the decimal separator is dropped, not rounded away.</summary>
    private static string SalesTaxKey(decimal vatRatePercent) =>
        "s_iva_" + vatRatePercent.ToString("0.##", CultureInfo.InvariantCulture).Replace(".", "", StringComparison.Ordinal);

    /// <summary>
    /// Null when the order can't identify a counterparty for a full factura (name, address, tax id). A foreign camp's tax
    /// id or passport stands in for a NIF, so the field is only checked for presence.
    /// </summary>
    private static InvoiceCounterparty? ResolveInvoiceCounterparty(Order order) =>
        string.IsNullOrWhiteSpace(order.CounterpartyName)
        || string.IsNullOrWhiteSpace(order.CounterpartyAddress)
        || string.IsNullOrWhiteSpace(order.CounterpartyVatId)
            ? null
            : new InvoiceCounterparty(
                order.CounterpartyName.Trim(),
                order.CounterpartyVatId.Trim(),
                order.CounterpartyAddress.Trim(),
                order.CounterpartyCountryCode?.Trim(),
                order.CounterpartyEmail?.Trim());

    private sealed record InvoiceCounterparty(
        string Name, string TaxId, string Address, string? CountryCode, string? Email);

}
