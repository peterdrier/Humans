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
    IHoldedClient holdedClient,
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

        if (!holdedClient.IsConfigured)
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
            if (GetRecoveryRefusal(order, adoptedDoc, totals) is { } recoveryRefusal)
                return Refuse(order, actorUserId, recoveryRefusal);
            if (adoptedDoc.IsDraft == true)
            {
                if (balanceRefusal is not null) return Refuse(order, actorUserId, balanceRefusal);

                // Created but not approved. A draft books no revenue and carries no number, so freezing against it
                // leaves the order invoiced with no legal invoice: finish the issuance instead.
                await holdedClient.ApproveSalesDocumentAsync(adoptedKind, adoptedDoc.Id, CancellationToken.None);
                adoptedDoc = await holdedClient.GetSalesDocumentAsync(
                    adoptedKind, adoptedDoc.Id, CancellationToken.None);
                if (GetRecoveryRefusal(order, adoptedDoc, totals) is { } approvalRefusal)
                    return Refuse(order, actorUserId, approvalRefusal);
            }
            return await CommitInvoiceAsync(
                order, adoptedDoc,
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
            contactId = await holdedClient.CreateContactAsync(
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

        var docId = await holdedClient.CreateSalesDocumentAsync(kind, input, CancellationToken.None);
        // A draft books no revenue, so approval is part of issuance.
        await holdedClient.ApproveSalesDocumentAsync(kind, docId, CancellationToken.None);
        var document = await holdedClient.GetSalesDocumentAsync(kind, docId, CancellationToken.None);

        return await CommitInvoiceAsync(
            order, document,
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
    /// The order stays Open after a failed attempt, so its lines and prices can move before the retry; adopting on the
    /// tag alone would freeze it against a document that says something else. Correcting an issued document is a
    /// factura rectificativa, a human decision, so this fails loudly.
    /// </summary>
    private static string? GetRecoveryRefusal(
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

        var name = string.IsNullOrEmpty(document.DocNumber) ? document.Id : document.DocNumber;
        return $"Holded already holds document {name} for order {order.Id}, but it no longer matches "
            + $"the order: the document reads EUR {document.Subtotal:0.00} + {document.Tax:0.00} VAT "
            + $"= {document.Total:0.00}, while the order now totals EUR {expectedSubtotal:0.00} + "
            + $"{expectedTax:0.00} VAT = {expectedTotal:0.00}. Resolve it by hand — correcting an issued document is a factura "
            + "rectificativa, and no second document will be issued in the meantime.";
    }

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
        var found = new List<(HoldedSalesDocumentKind Kind, HoldedSalesDocumentDto Document)>();
        foreach (var kind in new[] { HoldedSalesDocumentKind.Invoice, HoldedSalesDocumentKind.SalesReceipt })
        {
            foreach (var id in await holdedClient.FindSalesDocumentIdsByTagAsync(kind, tag, ct))
                found.Add((kind, await holdedClient.GetSalesDocumentAsync(kind, id, ct)));
        }
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
        string requestPayload,
        Guid actorUserId,
        string auditDetail,
        CancellationToken ct)
    {
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

        // items[].account is the chart account's id, not its 8-digit number, so resolve against the live chart on every
        // issue; that also picks up an account Acountax created minutes ago.
        var wanted = order.Lines
            .Select(l => products[l.ProductId].HoldedRevenueAccountNum!.Value)
            .Concat(needsDepositAccount ? [depositAccountNum!.Value] : Array.Empty<int>())
            .ToHashSet();
        var accountIdsByNum = (await holdedClient.ListAccountingAccountsAsync(ct))
            .Where(a => wanted.Contains(a.Number))
            .ToDictionary(a => a.Number, a => a.Id);

        var unknown = wanted.Where(n => !accountIdsByNum.ContainsKey(n)).ToList();
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
                AccountId = accountIdsByNum[product.HoldedRevenueAccountNum!.Value],
            });

            if (t.EffectiveDeposit is not { } depositPerUnit || depositPerUnit <= 0m) continue;
            lines.Add(new HoldedSalesDocumentLineInput
            {
                Name = $"{product.Name} — refundable deposit",
                Units = line.Qty,
                Price = depositPerUnit,
                Taxes = [SalesTaxKey(0m)],
                AccountId = accountIdsByNum[depositAccountNum!.Value],
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
