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

/// <summary>Creates, approves or recovers the invoice that freezes a billable Store order.</summary>
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
    /// Issues the camp order's consolidated Holded sales document and freezes the order.
    ///
    /// Pipeline: freeze the line snapshots at today's catalog price (#816) → resolve each
    /// product's revenue account → build the lines (goods at the product's VAT rate, deposits
    /// tax-0 to the fianzas liability account) → create the document and <b>approve</b> it (a
    /// draft books nothing) → write <c>store_invoices</c> with both payloads and flip the order
    /// to <see cref="OrderState.InvoiceIssued"/> in one save.
    ///
    /// Document kind: a full <i>factura</i> when counterparty details are on file, a
    /// <i>factura simplificada</i> (sales receipt) otherwise. Above
    /// <see cref="StoreSectionOptions.SimplifiedInvoiceThresholdEur"/> a receipt is not legal,
    /// so a counterparty-less order that large is refused rather than downgraded.
    ///
    /// Only issuable at a zero balance: a camp that still owes, or is owed, settles first.
    ///
    /// Idempotent on both sides: an order that already carries an <c>IssuedInvoiceId</c> throws
    /// without calling Holded, and — because a document approved by an attempt that then failed
    /// locally leaves no trace here — Holded is searched for a document already tagged with this
    /// order before anything is created. A match is adopted, never re-issued: a second approved
    /// factura is a real legal document, correctable only by a factura rectificativa. A recovered
    /// document is checked against the order's current totals first (divergence refuses loudly)
    /// and approved if the earlier attempt died between create and approve.
    /// </summary>
    [ExternalWrite]
    public async Task IssueAsync(Order order, Guid actorUserId, CancellationToken ct = default)
    {
        // Idempotency guard — deliberately before any Holded call, so a double-submit cannot
        // create a second document.
        if (order.IssuedInvoiceId is not null || order.State != OrderState.Open)
            throw new InvalidOperationException("This order has already been invoiced.");

        if (order.Lines.Count == 0)
            throw new InvalidOperationException("Cannot invoice an order with no lines.");

        if (!holdedClient.IsConfigured)
            throw new InvalidOperationException(
                "Holded is not configured in this environment, so no invoice can be issued.");

        // Freeze seam (#816): rewrite each snapshot from the live catalog before issuing, so the
        // document and the order agree forever after. BalanceCalculator already computed the
        // effective prices for an Open order — reuse them rather than re-deriving.
        var totals = BalanceCalculator.Compute(order, await orderReader.LoadCurrentPricesAsync(ct));
        if (totals.BalanceEur != 0m)
            throw new InvalidOperationException(
                $"An invoice can only be issued when the order balance is zero (currently EUR {totals.BalanceEur:0.00}).");
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            line.UnitPriceSnapshot = t.EffectiveUnitPrice;
            line.VatRateSnapshot = t.EffectiveVatRate;
            line.DepositAmountSnapshot = t.EffectiveDeposit;
        }

        // The local guard above cannot see a document approved by an attempt that then failed
        // before its save, so ask Holded before creating anything. Read-only, and it runs ahead of
        // the line-account resolution so that a catalog edited in between cannot block the
        // reconciliation of a document that already exists.
        var tag = OrderDocumentTag(order.Id);
        if (await FindAlreadyIssuedDocumentAsync(tag, ct) is { } alreadyIssued)
        {
            var (adoptedKind, adoptedDoc) = alreadyIssued;
            // Before anything is written, and before the order is frozen against it: the order
            // stayed Open, so it could have been edited or repriced since that document was
            // created. Divergence is a human's problem, not something to reconcile silently.
            EnsureRecoveredDocumentMatches(order, adoptedDoc, totals);
            if (adoptedDoc.IsDraft == true)
            {
                // Creation succeeded and approval did not. A draft books no revenue and carries no
                // sequential number, so freezing the order against it would leave the order
                // invoiced with no legal invoice behind it — finish the issuance instead.
                await holdedClient.ApproveSalesDocumentAsync(adoptedKind, adoptedDoc.Id, CancellationToken.None);
                adoptedDoc = await holdedClient.GetSalesDocumentAsync(
                    adoptedKind, adoptedDoc.Id, CancellationToken.None);
            }
            await CommitInvoiceAsync(
                order, adoptedDoc,
                JsonSerializer.Serialize(new { kind = adoptedKind.ToString(), adopted = true, tag }),
                actorUserId,
                $"Adopted Holded {DocumentKindName(adoptedKind)} {adoptedDoc.DocNumber} "
                + $"(EUR {adoptedDoc.Total:0.00}), already issued for order {order.Id} by an earlier "
                + "attempt that failed before saving — no second document was created",
                ct);
            return;
        }

        var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
        if (totalDue <= 0m)
            throw new InvalidOperationException("Cannot invoice an order with a zero total.");

        var products = (await repo.GetProductsByIdsAsync(
                order.Lines.Select(l => l.ProductId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);

        var documentLines = await BuildInvoiceLinesAsync(order, totalsByLine, products, ct);

        // A receipt carries no counterparty, so it is only lawful below the simplified-invoice
        // threshold. Above it the details are mandatory — refuse rather than silently issue the
        // wrong document type.
        var counterparty = ResolveInvoiceCounterparty(order);
        var kind = counterparty is null
            ? HoldedSalesDocumentKind.SalesReceipt
            : HoldedSalesDocumentKind.Invoice;
        if (counterparty is null && totalDue > options.Value.SimplifiedInvoiceThresholdEur)
            throw new InvalidOperationException(
                $"An order of EUR {totalDue:0.00} needs a full factura: fill in the counterparty's "
                + "name, address and tax id (or passport number) first.");

        var displayName = await orderReader.ResolveCounterpartyDisplayNameAsync(order, ct);
        string? contactId = null;
        if (counterparty is not null)
        {
            // Not the request token: everything from here on writes to Holded, and a half-created
            // contact/document has no local compensation (memory/architecture/cancellation-token-propagation.md).
            contactId = await holdedClient.UpsertContactAsync(
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
        // A draft books no revenue, so approval is part of issuance, not a later step.
        await holdedClient.ApproveSalesDocumentAsync(kind, docId, CancellationToken.None);
        var document = await holdedClient.GetSalesDocumentAsync(kind, docId, CancellationToken.None);

        await CommitInvoiceAsync(
            order, document,
            JsonSerializer.Serialize(new { kind = kind.ToString(), input }),
            actorUserId,
            $"Issued Holded {DocumentKindName(kind)} {document.DocNumber} "
            + $"for EUR {totalDue:0.00} on order {order.Id}",
            ct);
    }

    /// <summary>
    /// Refuses adoption when the recovered document no longer describes this order. The order stays
    /// <c>Open</c> after a failed attempt, so its lines can be edited and its catalog prices can
    /// move before the retry — adopting on the tag alone would freeze the order against a document
    /// that says something else, permanently. Correcting an issued document is a factura
    /// rectificativa, which is a human decision, so this fails loudly rather than choosing a side.
    /// </summary>
    private static void EnsureRecoveredDocumentMatches(
        Order order, HoldedSalesDocumentDto document, BalanceCalculator.Result totals)
    {
        // Deposits are ordinary tax-0 lines on the document, so they land in Holded's subtotal.
        var expectedSubtotal = decimal.Round(totals.LinesSubtotalEur + totals.DepositTotalEur, 2);
        var expectedTax = decimal.Round(totals.VatTotalEur, 2);
        var expectedTotal = decimal.Round(expectedSubtotal + expectedTax, 2);
        if (decimal.Round(document.Subtotal, 2) == expectedSubtotal
            && decimal.Round(document.Tax, 2) == expectedTax
            && decimal.Round(document.Total, 2) == expectedTotal)
            return;

        var name = string.IsNullOrEmpty(document.DocNumber) ? document.Id : document.DocNumber;
        throw new InvalidOperationException(
            $"Holded already holds document {name} for order {order.Id}, but it no longer matches "
            + $"the order: the document reads EUR {document.Subtotal:0.00} + {document.Tax:0.00} VAT "
            + $"= {document.Total:0.00}, while the order now totals EUR {expectedSubtotal:0.00} + "
            + $"{expectedTax:0.00} VAT = {expectedTotal:0.00}. The order was changed after that "
            + "document was created. Resolve it by hand — correcting an issued document is a factura "
            + "rectificativa, and no second document will be issued in the meantime.");
    }

    /// <summary>The tag every store sales document carries. Holded's list endpoints return
    /// <c>tags</c> but not <c>notes</c>, so this — not the human-readable note beside it — is what
    /// makes a document findable by the order it was issued for.</summary>
    private static string OrderDocumentTag(Guid orderId) => $"humans-order-{orderId}";

    private static string DocumentKindName(HoldedSalesDocumentKind kind) =>
        kind == HoldedSalesDocumentKind.Invoice ? "factura" : "factura simplificada";

    /// <summary>
    /// The sales document a previous attempt already created for this order, or null. Both kinds
    /// are searched in full: the counterparty details decide the kind and can be filled in between
    /// two attempts, so the earlier document may be of the other kind — and adopting it is still
    /// right, because what must not happen is a second approved document. When several match, an
    /// already-approved one wins over a draft: approving the draft beside it would book the revenue
    /// twice and hand out a second legal number.
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
            // The duplicate this guard exists to prevent has already happened — only a factura
            // rectificativa clears it. Adopting one still stops a third being issued.
            logger.LogWarning(
                "Holded holds {Count} documents tagged {Tag}: {Ids}. Adopting an approved one if there is any.",
                found.Count, tag, string.Join(", ", found.Select(f => $"{f.Kind}:{f.Document.Id}")));

        var approved = found.FirstOrDefault(f => f.Document.IsDraft != true);
        return approved.Document is not null ? approved : found[0];
    }

    /// <summary>Writes the <c>store_invoices</c> row and the frozen order in one save, then audits.
    /// Shared by first issuance and by adoption of an already-issued document — the two differ only
    /// in the request payload they can honestly record and in what the audit line says.</summary>
    private async Task CommitInvoiceAsync(
        Order order,
        HoldedSalesDocumentDto document,
        string requestPayload,
        Guid actorUserId,
        string auditDetail,
        CancellationToken ct)
    {
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
            AuditAction.StoreInvoiceIssued, AuditEntityTypes.Invoice, invoice.Id,
            auditDetail, actorUserId,
            order.Id, AuditEntityTypes.Order);
    }

    /// <summary>
    /// One document line per order line, plus a separate VAT-free line for each line that carries
    /// a deposit. Refundable deposits are a liability, not income, so they post tax-0 to the
    /// configured fianzas account instead of the item's revenue account.
    /// </summary>
    private async Task<IReadOnlyList<HoldedSalesDocumentLineInput>> BuildInvoiceLinesAsync(
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
            throw new InvalidOperationException(
                "These catalog items have no Holded revenue account yet: "
                + string.Join(", ", missingAccount)
                + ". Set it on /Store/Admin/Catalog before issuing.");

        var needsDepositAccount = order.Lines.Any(l => totalsByLine[l.Id].DepositEur > 0m);
        var depositAccountNum = options.Value.DepositLiabilityAccountNum;
        if (needsDepositAccount && depositAccountNum is null)
            throw new InvalidOperationException(
                "This order carries refundable deposits but no deposit liability account is "
                + "configured (Store:DepositLiabilityAccountNum). Deposits are not income and "
                + "must not be booked to a revenue account.");

        // Holded's items[].account is the chart account's *id*, not its 8-digit number, so the
        // numbers StoreAdmin holds are resolved against the live chart on every issue — a fresh
        // read also picks up an account Acountax created minutes ago.
        var wanted = order.Lines
            .Select(l => products[l.ProductId].HoldedRevenueAccountNum!.Value)
            .Concat(needsDepositAccount ? [depositAccountNum!.Value] : Array.Empty<int>())
            .ToHashSet();
        var accountIdsByNum = (await holdedClient.ListAccountingAccountsAsync(ct))
            .Where(a => wanted.Contains(a.Number))
            .ToDictionary(a => a.Number, a => a.Id);

        var unknown = wanted.Where(n => !accountIdsByNum.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
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
        return lines;
    }

    /// <summary>
    /// Holded's sales tax key for a VAT rate: 21 → <c>s_iva_21</c>, 7.5 → <c>s_iva_75</c>,
    /// 0 → <c>s_iva_0</c> (the decimal separator is dropped, not rounded away).
    /// </summary>
    private static string SalesTaxKey(decimal vatRatePercent) =>
        "s_iva_" + vatRatePercent.ToString("0.##", CultureInfo.InvariantCulture).Replace(".", "", StringComparison.Ordinal);

    /// <summary>
    /// The identified counterparty a full factura needs, or null when the order does not carry
    /// enough to identify one. Spanish law wants name + address + a tax id; a foreign camp's
    /// home-country tax id or passport number stands in for a NIF, so the field is only ever
    /// checked for presence.
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
