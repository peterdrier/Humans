using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Stripe.Contracts;
using NodaTime;
using System.Globalization;
using System.Text.Json;

namespace Humans.Store.Services;

internal sealed class InvoiceService(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IStripeService stripeService,
    IFinanceService finance,
    Service orders,
    ILogger<InvoiceService> logger) : IApplicationService
{

    /// <summary>Issues and approves the invoice, then books the order's money onto it in Holded at once. A refused
    /// issuance books nothing; a booking that waits or stops leaves the invoice issued, with the reason on the order.</summary>
    [ExternalWrite]
    public async Task<AdminMutationResult> IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var result = await IssueInvoiceCoreAsync(orderId, actorUserId, ct);
        if (result.Succeeded) await BookAsync(orderId, actorUserId, CancellationToken.None);
        return result;
    }

    /// <summary>What issuing would invoice, and why it may not yet; null for an order that can't be invoiced at all.</summary>
    public async Task<StoreInvoicePlan?> GetInvoicePlanAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null || order.TeamId is not null || order.State != OrderState.Open || order.Lines.Count == 0)
            return null;
        var totals = BalanceCalculator.Compute(order, await orders.LoadCurrentPricesAsync(order.Year, ct));
        var products = await repo.GetProductsByIdsAsync(order.Lines.Select(l => l.ProductId).Distinct().ToList(), ct);
        var names = order.Lines.Select(l => l.ProductId).Distinct().ToDictionary(
            id => id, id => products.FirstOrDefault(p => p.Id == id)?.Name ?? "(unknown product)");
        return StoreInvoicePlan.Build(order, totals, names);
    }

    /// <summary>The order's invoice and its booking state; null before issuance.</summary>
    public async Task<Invoice?> GetInvoiceAsync(Guid orderId, CancellationToken ct = default) =>
        (await repo.GetInvoicesForOrdersAsync([orderId], ct)).SingleOrDefault();

    [ExternalWrite]
    public async Task<AdminMutationResult> ResendInvoiceAsync(Guid orderId, Guid actorUserId)
    {
        var order = await repo.GetOrderByIdAsync(orderId, CancellationToken.None);
        var invoice = await GetInvoiceAsync(orderId, CancellationToken.None);
        if (order is null || invoice is null) return AdminMutationResult.Refused("This order has no invoice yet.");
        return await SendAsync(order, invoice, actorUserId, CancellationToken.None);
    }

    /// <summary>The invoice's PDF, rendered by Holded on request; Humans keeps no copy.</summary>
    public async Task<(byte[] Pdf, string DocNumber)?> GetInvoicePdfAsync(Guid orderId, CancellationToken ct = default)
    {
        var invoice = await GetInvoiceAsync(orderId, ct);
        if (invoice is null) return null;
        var pdf = await holded.GetSalesDocumentPdfAsync(
            DocumentKind(invoice), invoice.HoldedDocId, ct);
        return (pdf, invoice.HoldedDocNumber);
    }

    /// <summary>
    /// Freezes the line snapshots at today's catalog price, then creates and <b>approves</b> the Holded document (a draft
    /// books nothing) and flips the order to <see cref="OrderState.InvoiceIssued"/> in one save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full factura when counterparty details are on file, else a factura simplificada. Above
    /// <see cref="StoreSettings.SimplifiedInvoiceThresholdEur"/> a receipt isn't legal, so a counterparty-less
    /// order that large is refused rather than downgraded.
    /// </para>
    /// <para>
    /// Idempotent on both sides: a document approved by an attempt that then failed locally leaves no trace here, so
    /// Holded is searched for one tagged with this order first. A match is adopted, never re-issued: a second approved
    /// factura is a real legal document, correctable only by a factura rectificativa.
    /// </para>
    /// </remarks>
    [ExternalWrite]
    private async Task<AdminMutationResult> IssueInvoiceCoreAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        if (order is null)
            return Refuse(orderId, actorUserId, "Order not found.");
        if (order.TeamId is not null)
            return Refuse(orderId, actorUserId, "Team orders are non-billable.");

        // Before any Holded call, so a double-submit can't create a second document.
        if (order.IssuedInvoiceId is not null || order.State != OrderState.Open)
            return Refuse(order.Id, actorUserId, "This order has already been invoiced.");

        if (order.Lines.Count == 0)
            return Refuse(order.Id, actorUserId, "Cannot invoice an order with no lines.");

        if (!holded.IsConfigured)
            return Refuse(order.Id, actorUserId,
                "Holded is not configured in this environment, so no invoice can be issued.");

        // Freeze seam: rewrite each snapshot from the live catalog so the document and the order agree forever after.
        var totals = BalanceCalculator.Compute(order, await orders.LoadCurrentPricesAsync(order.Year, ct));
        var totalsByLine = totals.Lines.ToDictionary(t => t.LineId);
        foreach (var line in order.Lines)
        {
            var t = totalsByLine[line.Id];
            line.UnitPriceSnapshot = t.EffectiveUnitPrice;
            line.VatRateSnapshot = t.EffectiveVatRate;
            line.DepositAmountSnapshot = t.EffectiveDeposit;
        }

        var products = (await repo.GetProductsByIdsAsync(
                order.Lines.Select(l => l.ProductId).Distinct().ToList(), ct))
            .ToDictionary(p => p.Id);
        var plan = StoreInvoicePlan.Build(order, totals, ProductNames(order, products));

        // Recovery precedes the gate and line-account checks: payments or catalog assignments can change after Holded
        // approves a document whose local save failed.
        var tag = OrderDocumentTag(order.Id);
        if (await FindAlreadyIssuedDocumentAsync(tag, ct) is { } alreadyIssued)
        {
            var (adoptedKind, adoptedDoc) = alreadyIssued;
            // The order stayed Open, so it may have been edited or repriced since that document was created. Divergence
            // is a human's problem, not something to reconcile silently.
            if ((GetDocumentTotalsRefusal(order, adoptedDoc, plan)
                 ?? GetRecoveredLinesRefusal(order, adoptedDoc, plan)) is { } recoveryRefusal)
                return Refuse(order.Id, actorUserId, recoveryRefusal);
            if (adoptedDoc.IsDraft == true)
            {
                if (plan.Refusal is not null) return Refuse(order.Id, actorUserId, plan.Refusal);

                // Created but not approved. A draft books no revenue and carries no number, so freezing against it
                // leaves the order invoiced with no legal invoice: finish the issuance instead.
                adoptedDoc = await holdedWrite.ApproveSalesDocumentAsync(
                    adoptedKind, adoptedDoc.Id, CancellationToken.None);
            }
            return await CommitInvoiceAsync(
                order, adoptedDoc, plan,
                JsonSerializer.Serialize(new { kind = adoptedKind.ToString(), adopted = true, tag }),
                actorUserId,
                $"Adopted Holded {DocumentKindName(adoptedKind)} {adoptedDoc.DocNumber} "
                + $"(EUR {adoptedDoc.Total:0.00}), already issued by an earlier "
                + "attempt that failed before saving — no second document was created",
                ct);
        }

        if (plan.Refusal is not null) return Refuse(order.Id, actorUserId, plan.Refusal);

        var totalDue = plan.TotalEur;
        if (totalDue <= 0m)
            return Refuse(order.Id, actorUserId, "Cannot invoice an order with a zero total.");

        var settings = await repo.GetSettingsAsync(ct);
        var (documentLines, lineRefusal) = await BuildInvoiceLinesAsync(plan, products, ct);
        if (lineRefusal is not null) return Refuse(order.Id, actorUserId, lineRefusal);

        // A receipt carries no counterparty, so it's only lawful below the simplified-invoice threshold.
        var counterparty = ResolveInvoiceCounterparty(order);
        var kind = counterparty is null
            ? HoldedSalesDocumentKind.SalesReceipt
            : HoldedSalesDocumentKind.Invoice;
        if (counterparty is null && totalDue > settings.SimplifiedInvoiceThresholdEur)
            return Refuse(order.Id, actorUserId,
                $"An order of EUR {totalDue:0.00} needs a full factura: fill in the counterparty's "
                + "name, address and tax id (or passport number) first.");
        // Money beyond the total books as a collection and a refund as a payment, both on the contact a receipt lacks.
        if (counterparty is null && order.Payments.Any(p => p.Method == PaymentMethod.Refund && p.Status != PaymentStatus.Failed))
            return Refuse(order.Id, actorUserId,
                "This order had a refund, which can only be booked against a contact: fill in the counterparty's "
                + "name, address and tax id (or passport number) so it gets a full factura.");

        var displayName = await orders.ResolveCounterpartyDisplayNameAsync(order, ct);
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
            Description = $"{displayName} — Nobodies Collective store",
            Notes = plan.Notes(displayName, order.Id),
            Tags = [tag],
            Lines = documentLines,
            DiscountPercent = plan.DiscountPercent == 0m ? null : plan.DiscountPercent,
        };

        // A draft books no revenue, so issuing approves it.
        var document = await holdedWrite.IssueSalesDocumentAsync(kind, input, CancellationToken.None);

        return await CommitInvoiceAsync(
            order, document, plan,
            JsonSerializer.Serialize(new { kind = kind.ToString(), input }),
            actorUserId,
            $"Issued Holded {DocumentKindName(kind)} {document.DocNumber} "
            + $"for EUR {totalDue:0.00}",
            ct);
    }

    private AdminMutationResult Refuse(Guid orderId, Guid actorUserId, string reason)
    {
        logger.LogWarning("Store invoice rejected for order {OrderId}, actor {ActorUserId}: {Reason}",
            orderId, actorUserId, reason);
        return AdminMutationResult.Refused(reason);
    }

    /// <summary>
    /// Freezing against different totals would leave the order contradicting its legal invoice. A retry's lines and
    /// prices may also have changed since creation. Correcting an issued document is a factura rectificativa, a human
    /// decision, so both new issuance and recovery fail loudly. With a discount the subtotal isn't compared: whether
    /// Holded reports it before or after the discount is unverified, and VAT and total already pin the document.
    /// </summary>
    private static string? GetDocumentTotalsRefusal(
        Order order, HoldedSalesDocumentDto document, StoreInvoicePlan plan)
    {
        if ((plan.DiscountPercent != 0m || decimal.Round(document.Subtotal, 2) == plan.SubtotalEur)
            && decimal.Round(document.Tax, 2) == plan.TaxEur
            && decimal.Round(document.Total, 2) == plan.TotalEur)
            return null;

        return $"Holded already holds document {DocumentName(document)} for order {order.Id}, but it no longer matches "
            + $"the order: the document reads EUR {document.Subtotal:0.00} + {document.Tax:0.00} VAT "
            + $"= {document.Total:0.00}, while the order now totals EUR {plan.SubtotalEur:0.00} + "
            + $"{plan.TaxEur:0.00} VAT = {plan.TotalEur:0.00}. Resolve it by hand — correcting an issued document is a factura "
            + "rectificativa, and no second document will be issued in the meantime.";
    }

    /// <summary>
    /// Two lines can trade amounts with every total unchanged, so a recovered document's line set (quantity, unit price,
    /// VAT rate) must match too; names are not compared, since a renamed product is the same sale. Recovery only, before
    /// any approval: the line read-back is unverified against a live v2 document, and once this attempt has approved a
    /// document a wrong refusal would leave a legal factura behind an order that can never freeze.
    /// </summary>
    private static string? GetRecoveredLinesRefusal(
        Order order, HoldedSalesDocumentDto document, StoreInvoicePlan plan)
    {
        if (LineSet(plan.Lines.Select(l => new SalesLine(l.Units, l.Price, l.VatRate))).SequenceEqual(
                LineSet(document.Lines.Select(l => new SalesLine(l.Units, l.Price, l.TaxPercent)))))
            return null;

        return $"Holded already holds document {DocumentName(document)} for order {order.Id}, but its lines "
            + (document.Lines.Count == 0 ? "could not be read" : "no longer match the order's quantities, unit prices or VAT rates")
            + ", though the totals agree. Resolve it by hand — correcting an issued document is a factura rectificativa, "
            + "and no second document will be issued in the meantime.";
    }

    private static string DocumentName(HoldedSalesDocumentDto document) =>
        string.IsNullOrEmpty(document.DocNumber) ? document.Id : document.DocNumber;

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
        StoreInvoicePlan plan,
        string requestPayload,
        Guid actorUserId,
        string auditDetail,
        CancellationToken ct)
    {
        if (GetDocumentTotalsRefusal(order, document, plan) is { } totalsRefusal)
            return Refuse(order.Id, actorUserId, totalsRefusal);
        if (document.IsDraft == true || string.IsNullOrWhiteSpace(document.DocNumber))
            return Refuse(order.Id, actorUserId,
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

    /// <summary>Every line, kept deposits included, books to its product's revenue account.</summary>
    private async Task<(IReadOnlyList<HoldedSalesDocumentLineInput> Lines, string? Refusal)> BuildInvoiceLinesAsync(
        StoreInvoicePlan plan,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken ct)
    {
        var missingAccount = plan.Lines
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

        // Holded resolves each number to its chart id when issuing; syncing the chart first picks up an account Acountax
        // created minutes ago.
        var wanted = plan.Lines.Select(l => products[l.ProductId].HoldedRevenueAccountNum!.Value).ToHashSet();
        await holded.SyncChartAsync(ct);
        var chart = (await holded.GetChartAsync(ct)).Select(a => a.Number).ToHashSet();

        var unknown = wanted.Where(n => !chart.Contains(n)).ToList();
        if (unknown.Count > 0)
            return ([],
                "These accounts do not exist in Holded's chart of accounts: "
                + string.Join(", ", unknown.OrderBy(n => n))
                + ". Ask Acountax to create them, then re-issue.");

        return (plan.Lines.Select(l => new HoldedSalesDocumentLineInput
        {
            Name = l.Name,
            Units = l.Units,
            Price = l.Price,
            Taxes = [SalesTaxKey(l.VatRate)],
            AccountNum = products[l.ProductId].HoldedRevenueAccountNum!.Value,
        }).ToList(), null);
    }

    private static IReadOnlyDictionary<Guid, string> ProductNames(Order order, IReadOnlyDictionary<Guid, Product> products) =>
        order.Lines.Select(l => l.ProductId).Distinct()
            .ToDictionary(id => id, id => products.GetValueOrDefault(id)?.Name ?? "(unknown product)");

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

    /// <summary>How far before a payment's own date its feed line may be booked.</summary>
    private const int FeedLeadDays = 7;

    private static readonly string[] ReconciledStatuses = ["reconciled", "forced_reconciled"];

    private sealed record Leg(Payment Payment, string TreasuryId, bool IsStripe)
    {
        public bool IsRefund => Payment.AmountEur < 0m;
    }

    private sealed record ResolvedLeg(Leg Leg, HoldedBankMovementDto Line);

    /// <summary>Not the request token from here on: every step writes to Holded.</summary>
    [ExternalWrite]
    public async Task<InvoiceBookingState> BookAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        var invoice = order?.IssuedInvoiceId is null ? null
            : (await repo.GetInvoicesForOrdersAsync([orderId], ct)).SingleOrDefault();
        if (order is null || invoice is null) return InvoiceBookingState.NotBooked;
        if (!holded.IsConfigured) return invoice.BookingState ?? InvoiceBookingState.NotBooked;
        // Collections and refund payments have no key to find them by, so a rerun after a stop that posted could
        // post them twice: that order is put right in Holded by hand.
        if (invoice.BookingState is InvoiceBookingState.Booked or InvoiceBookingState.NeedsReview)
            return invoice.BookingState.Value;

        var posted = new List<string>();
        try
        {
            var (state, note) = await RunAsync(order, invoice, posted, ct);
            return await FinishAsync(order, invoice, state, note, posted, actorUserId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || posted.Count > 0)
        {
            // Whatever failed, what was already posted is recorded and audited.
            logger.LogWarning(ex, "Store invoice booking for order {OrderId} stopped", orderId);
            return await FinishAsync(order, invoice, InvoiceBookingState.Stopped,
                $"A booking step failed: {ex.Message}", posted, actorUserId);
        }
    }

    private async Task<(InvoiceBookingState State, string? Note)> RunAsync(
        Order order, Invoice invoice, List<string> posted, CancellationToken ct)
    {
        var kind = DocumentKind(invoice);
        var gateway = (await repo.GetSettingsAsync(ct)).StripeGatewayTreasuryId;
        var bank = finance.BankTreasuryAccountId;

        var legs = new List<Leg>();
        foreach (var p in order.Payments.Where(p => p.Method is PaymentMethod.Stripe or PaymentMethod.BankTransfer
                     or PaymentMethod.Refund or PaymentMethod.Manual))
        {
            if (p.Status == PaymentStatus.Failed || (p.Status == PaymentStatus.Pending && p.AmountEur > 0m)) continue;
            if (p.Status == PaymentStatus.Pending)
                return (InvoiceBookingState.WaitingForFeed, "A refund is still pending in Stripe.");
            if (p.Method == PaymentMethod.Manual)
                return (InvoiceBookingState.Stopped,
                    $"Payment of EUR {p.AmountEur:0.00} is a manual one; it names no treasury to book it on.");

            var isStripe = p.Method == PaymentMethod.Stripe
                || (p.Method == PaymentMethod.Refund && p.ExternalRef?.StartsWith("re_", StringComparison.Ordinal) == true);
            var treasury = isStripe ? gateway : bank;
            if (string.IsNullOrWhiteSpace(treasury))
                return (InvoiceBookingState.Stopped, isStripe
                    ? "The Store's Stripe gateway treasury is not set on the Settings page, Store tab."
                    : "The bank treasury (Sepa:TreasuryAccountId) is not configured.");
            legs.Add(new Leg(p, treasury, isStripe));
        }

        // Feed lines first, before anything is posted: a missing one means booking nothing at all.
        var resolved = await ResolveFeedLinesAsync(legs, ct);
        if (resolved.Waiting.Count > 0)
            return (InvoiceBookingState.WaitingForFeed,
                "Waiting for the feed: " + string.Join("; ", resolved.Waiting) + ".");
        if (resolved.Stop is not null) return (InvoiceBookingState.Stopped, resolved.Stop);

        var document = await holded.GetSalesDocumentAsync(kind, invoice.HoldedDocId, ct);
        var unclaimed = document.Payments.ToList();
        var remaining = document.Total;
        var description = $"Store order {order.Id}";

        // Oldest money first, so a rerun splits every charge the same way.
        foreach (var (leg, line) in resolved.Lines.Where(r => !r.Leg.IsRefund)
                     .OrderBy(r => r.Line.Date).ThenBy(r => r.Line.BookedAt).ThenBy(r => r.Line.Id, StringComparer.Ordinal))
        {
            var amount = leg.Payment.AmountEur;
            var share = Math.Min(amount, remaining);
            remaining -= share;
            var excess = amount - share;
            var onInvoice = share > 0m ? Claim(unclaimed, share, leg.TreasuryId, line.Date) : null;
            if (IsReconciled(line)) continue;

            var refs = new List<HoldedReconcileDocumentRef>();
            if (share > 0m)
            {
                var paymentId = onInvoice?.Id ?? await holdedWrite.PaySalesDocumentAsync(
                    kind, document.Id, share, leg.TreasuryId, line.Date, description, CancellationToken.None);
                if (onInvoice is null) posted.Add($"invoice payment EUR {share:0.00} ({paymentId})");
                if (IsUnconfirmed(paymentId))
                    return (InvoiceBookingState.Stopped,
                        $"Holded took the invoice payment of EUR {share:0.00} but returned no id to reconcile it with.");
                refs.Add(new HoldedReconcileDocumentRef(paymentId, HoldedReconcileDocumentType.Payment));
            }
            if (excess > 0m)
            {
                if (document.ContactId is null)
                    return (InvoiceBookingState.Stopped,
                        $"EUR {excess:0.00} was paid beyond the invoice, but the invoice has no contact to book it on.");
                var collectionId = await holdedWrite.CreateTreasuryPaymentAsync(
                    HoldedTreasuryPaymentKind.Collection, document.ContactId, leg.TreasuryId, excess, line.Date,
                    description, CancellationToken.None);
                posted.Add($"collection EUR {excess:0.00} ({collectionId})");
                if (IsUnconfirmed(collectionId))
                    return (InvoiceBookingState.Stopped,
                        $"Holded took the collection of EUR {excess:0.00} but returned no id to reconcile it with.");
                refs.Add(new HoldedReconcileDocumentRef(collectionId, HoldedReconcileDocumentType.Payment));
            }
            await holdedWrite.ReconcileBankMovementAsync(leg.TreasuryId, line.Id, refs, CancellationToken.None);
            posted.Add($"reconciled feed line {line.Id}");
        }

        foreach (var (leg, line) in resolved.Lines.Where(r => r.Leg.IsRefund))
        {
            if (IsReconciled(line)) continue;
            if (document.ContactId is null)
                return (InvoiceBookingState.Stopped,
                    $"A refund of EUR {-leg.Payment.AmountEur:0.00} has no contact on the invoice to book it on.");
            var paymentId = await holdedWrite.CreateTreasuryPaymentAsync(
                HoldedTreasuryPaymentKind.Payment, document.ContactId, leg.TreasuryId, -leg.Payment.AmountEur,
                line.Date, description, CancellationToken.None);
            posted.Add($"refund payment EUR {-leg.Payment.AmountEur:0.00} ({paymentId})");
            if (IsUnconfirmed(paymentId))
                return (InvoiceBookingState.Stopped,
                    $"Holded took the refund payment of EUR {-leg.Payment.AmountEur:0.00} but returned no id to reconcile it with.");
            await holdedWrite.ReconcileBankMovementAsync(leg.TreasuryId, line.Id,
                [new HoldedReconcileDocumentRef(paymentId, HoldedReconcileDocumentType.Payment)], CancellationToken.None);
            posted.Add($"reconciled feed line {line.Id}");
        }

        return await EndCheckAsync(kind, invoice, legs, ct);
    }

    private async Task<(InvoiceBookingState, string?)> EndCheckAsync(
        HoldedSalesDocumentKind kind, Invoice invoice, IReadOnlyList<Leg> legs, CancellationToken ct)
    {
        var problems = new List<string>();
        var document = await holded.GetSalesDocumentAsync(kind, invoice.HoldedDocId, ct);
        if (document.PaymentsPending is not 0m)
            problems.Add(document.PaymentsPending is { } pending
                ? $"the invoice still shows EUR {pending:0.00} pending"
                : "the invoice did not say what is still pending");

        if (document.ContactAccountNum is { } account)
        {
            if (!await holded.SyncAccountLedgerAsync(account, ct))
                problems.Add($"account {account} could not be re-read from Holded (a sync was running)");
            else if ((await holded.GetAccountBalancesAsync(ct)).GetValueOrDefault(account) is var balance && balance != 0m)
                problems.Add($"the camp's account {account} reads EUR {balance:0.00}, not 0.00");
        }
        else if (document.ContactId is not null)
            problems.Add("the invoice's contact has no 430 account to check");

        var after = await ResolveFeedLinesAsync(legs, ct);
        var open = after.Lines.Where(r => !IsReconciled(r.Line)).Select(r => r.Line.Id).ToList();
        if (after.Stop is not null || after.Waiting.Count > 0) problems.Add("the feed could not be re-read");
        else if (open.Count > 0) problems.Add("feed lines not reconciled: " + string.Join(", ", open));

        return problems.Count == 0
            ? (InvoiceBookingState.Booked, null)
            : (InvoiceBookingState.Stopped, "End check failed: " + string.Join("; ", problems) + ". Nothing more was posted.");
    }

    private async Task<(IReadOnlyList<ResolvedLeg> Lines, IReadOnlyList<string> Waiting, string? Stop)>
        ResolveFeedLinesAsync(IReadOnlyList<Leg> legs, CancellationToken ct)
    {
        var lines = new List<ResolvedLeg>();
        var waiting = new List<string>();
        if (legs.Count == 0) return (lines, waiting, null);

        var today = clock.GetCurrentInstant().InUtc().Date;
        var feeds = new Dictionary<string, IReadOnlyList<HoldedBankMovementDto>>(StringComparer.Ordinal);
        foreach (var group in legs.GroupBy(l => l.TreasuryId, StringComparer.Ordinal))
        {
            var from = group.Min(l => l.Payment.ReceivedAt).InUtc().Date.PlusDays(-FeedLeadDays);
            if (!await holded.SyncBankMovementsAsync(group.Key, from, today, ct: ct))
                return (lines, ["Holded's feed is being synced by something else, retry in a minute"], null);
            feeds[group.Key] = (await holded.GetBankMovementsAsync(group.Key, from, today, ct)).Movements;
        }

        foreach (var leg in legs)
        {
            var feed = feeds[leg.TreasuryId];
            var p = leg.Payment;
            List<HoldedBankMovementDto> matches;
            if (!leg.IsStripe)
            {
                matches = feed.Where(m => string.Equals(m.Id, p.ExternalRef, StringComparison.Ordinal)).ToList();
                if (matches.Count == 1 && matches[0].Amount != p.AmountEur)
                    return (lines, waiting, $"Bank line {p.ExternalRef} is EUR {matches[0].Amount:0.00}, "
                        + $"but its payment on the order is EUR {p.AmountEur:0.00}.");
            }
            else
            {
                var (amount, created) = await StripeAmountAndCreatedAsync(p, legs.Select(l => l.Payment).ToList(), ct);
                if (created is null)
                    return (lines, waiting, $"Stripe would not say when {Describe(p)} was made.");
                if (amount != p.AmountEur)
                    return (lines, waiting, $"Stripe shows EUR {amount:0.00} for {Describe(p)}, "
                        + $"but the order records EUR {p.AmountEur:0.00}.");
                matches = feed.Where(m => m.Amount == amount && m.BookedAt == created).ToList();
            }

            switch (matches.Count)
            {
                case 1: lines.Add(new ResolvedLeg(leg, matches[0])); break;
                case 0: waiting.Add(Describe(p)); break;
                default:
                    return (lines, waiting, $"Several feed lines match {Describe(p)}: "
                    + string.Join(", ", matches.Select(m => m.Id)) + ".");
            }
        }
        return (lines, waiting, null);
    }

    /// <summary>A charge's line is the charge's gross at its creation second; a refund's is the refund's, read from
    /// Stripe by its id, since a refund recorded by hand carries the entry time, not Stripe's.</summary>
    private async Task<(decimal Amount, Instant? Created)> StripeAmountAndCreatedAsync(
        Payment p, IReadOnlyList<Payment> orderPayments, CancellationToken ct)
    {
        if (p.AmountEur < 0m)
        {
            foreach (var pi in orderPayments.Select(o => o.StripePaymentIntentId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var refund = (await stripeService.ListStoreRefundsAsync(pi, ct))?
                    .FirstOrDefault(r => string.Equals(r.RefundId, p.ExternalRef, StringComparison.Ordinal));
                if (refund is not null) return (-refund.AmountEur, refund.CreatedAt);
            }
            return (p.AmountEur, null);
        }
        if (p.StripePaymentIntentId is not { } chargeIntent) return (p.AmountEur, null);
        var charge = await stripeService.GetStoreChargeAsync(chargeIntent, ct);
        return charge is null ? (p.AmountEur, null) : (charge.AmountEur, charge.CreatedAt);
    }

    private static string Describe(Payment p) => p.AmountEur < 0m
        ? $"refund of EUR {-p.AmountEur:0.00} ({p.ExternalRef})"
        : $"{(p.Method == PaymentMethod.Stripe ? "Stripe charge" : "bank transfer")} of EUR {p.AmountEur:0.00} "
          + $"({p.StripePaymentIntentId ?? p.ExternalRef})";

    private static HoldedPaymentDetailDto? Claim(
        List<HoldedPaymentDetailDto> unclaimed, decimal amount, string treasuryId, LocalDate date)
    {
        var match = unclaimed.FirstOrDefault(p => p.Amount == amount && p.Date == date
            && string.Equals(p.BankId, treasuryId, StringComparison.Ordinal));
        if (match is not null) unclaimed.Remove(match);
        return match;
    }

    private static bool IsReconciled(HoldedBankMovementDto line) =>
        ReconciledStatuses.Contains(line.Status, StringComparer.Ordinal);

    private static bool IsUnconfirmed(string paymentRef) =>
        paymentRef.StartsWith("unconfirmed:", StringComparison.Ordinal);

    private async Task<InvoiceBookingState> FinishAsync(
        Order order, Invoice invoice, InvoiceBookingState state, string? note, IReadOnlyList<string> posted,
        Guid actorUserId)
    {
        if (state == InvoiceBookingState.Stopped && posted.Count > 0)
        {
            state = InvoiceBookingState.NeedsReview;
            note += " Already posted: " + string.Join("; ", posted) + ". Finish this order in Holded by hand.";
        }
        var changed = invoice.BookingState != state || !string.Equals(invoice.BookingNote, note, StringComparison.Ordinal) || posted.Count > 0;
        invoice.BookingState = state;
        invoice.BookingNote = note;
        await repo.SaveInvoiceBookingAsync(invoice, CancellationToken.None);
        if (changed)
            await audit.LogAsync(
                StoreAuditActions.StoreInvoiceBooked, AuditEntityTypes.Invoice, invoice.Id,
                $"Booking of invoice {invoice.HoldedDocNumber}: {state}"
                    + (note is null ? string.Empty : $" — {note}")
                    + (posted.Count == 0 ? " (nothing posted)" : $". Posted: {string.Join("; ", posted)}"),
                actorUserId, order.Id, AuditEntityTypes.Order);

        if (state == InvoiceBookingState.Booked && invoice.SentAt is null)
            await SendAsync(order, invoice, actorUserId, CancellationToken.None);
        return state;
    }

    /// <summary>Holded emails the invoice to the order's counterparty email, else to its contact's addresses. A receipt
    /// with neither can't be sent.</summary>
    public async Task<AdminMutationResult> SendAsync(Order order, Invoice invoice, Guid actorUserId, CancellationToken ct)
    {
        var kind = DocumentKind(invoice);
        var email = string.IsNullOrWhiteSpace(order.CounterpartyEmail) ? null : order.CounterpartyEmail.Trim();
        if (email is null && kind == HoldedSalesDocumentKind.SalesReceipt)
            return AdminMutationResult.Refused("The order has no email address to send the invoice to.");
        try
        {
            await holdedWrite.SendSalesDocumentAsync(kind, invoice.HoldedDocId, email is null ? null : [email], ct);
        }
        catch (HoldedApiException ex)
        {
            logger.LogWarning("Sending invoice {DocNumber} for order {OrderId} failed: {Message}",
                invoice.HoldedDocNumber, order.Id, ex.Message);
            return AdminMutationResult.Refused($"Holded could not send the invoice: {ex.Message}");
        }

        invoice.SentAt = clock.GetCurrentInstant();
        await repo.SaveInvoiceBookingAsync(invoice, CancellationToken.None);
        await audit.LogAsync(
            StoreAuditActions.StoreInvoiceSent, AuditEntityTypes.Invoice, invoice.Id,
            $"Sent invoice {invoice.HoldedDocNumber} to {email ?? "the invoice contact's email"}",
            actorUserId, order.Id, AuditEntityTypes.Order);
        return AdminMutationResult.Success;
    }

    /// <summary>Issuance records the kind it used in the request payload; documents issued before kept only that.</summary>
    internal static HoldedSalesDocumentKind DocumentKind(Invoice invoice)
    {
        try
        {
            using var json = JsonDocument.Parse(invoice.RequestPayload);
            if (json.RootElement.TryGetProperty("kind", out var k)
                && Enum.TryParse<HoldedSalesDocumentKind>(k.GetString(), out var kind))
                return kind;
        }
        catch (JsonException)
        {
            // Fall through to the default.
        }
        return HoldedSalesDocumentKind.Invoice;
    }
}
