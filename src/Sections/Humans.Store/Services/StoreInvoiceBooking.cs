using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Data;
using Humans.Store.Domain;
using Humans.Stripe.Contracts;
using NodaTime;

namespace Humans.Store.Services;

/// <summary>
/// Puts an invoiced camp order's money onto its invoice in Holded in one go, right after approval, and again on Retry.
/// Each charge pays the invoice up to exactly its total; what a charge brings beyond that is a collection on the camp's
/// contact, and each refund a payment back to it. Every leg is reconciled against its feed line: the Store's Stripe
/// gateway for Stripe money, the bank (Sabadell) for bank money. Then the end check: the invoice owes nothing, the camp's
/// 430 is at zero and every feed line of the order is reconciled.
/// </summary>
/// <remarks>
/// <para>
/// A gateway feed line carries no Stripe id: it is the line whose amount and <c>booking_date</c> (to the second) equal
/// the charge's or refund's. A bank leg's line is the movement id stored as the payment's reference.
/// </para>
/// <para>
/// Idempotent: a reconciled feed line is skipped, and a payment already on the invoice (same amount, treasury and date)
/// is reused, so a rerun or an order booked by hand is a no-op. If any leg's line isn't in Holded's feed yet, nothing is
/// booked and the order waits. Holded takes no refund on an invoice, links no API credit note to one, and a payment's
/// amount is never edited.
/// </para>
/// </remarks>
internal sealed class StoreInvoiceBooking(
    IStoreRepository repo,
    IAuditLogService audit,
    IClock clock,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IStripeService stripe,
    IFinanceService finance,
    ILogger logger) : IApplicationService
{
    /// <summary>How far before a payment's own date its feed line may be booked.</summary>
    private const int FeedLeadDays = 7;

    private static readonly string[] ReconciledStatuses = ["reconciled", "forced_reconciled"];

    private sealed record Leg(Payment Payment, string TreasuryId, bool IsStripe)
    {
        public bool IsRefund => Payment.AmountEur < 0m;
    }

    private sealed record ResolvedLeg(Leg Leg, HoldedBankMovementDto Line);

    /// <summary>Not the request token from here on: every step writes to Holded.</summary>
    public async Task<InvoiceBookingState> BookAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
    {
        var order = await repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
        var invoice = order?.IssuedInvoiceId is null ? null
            : (await repo.GetInvoicesForOrdersAsync([orderId], ct)).SingleOrDefault();
        if (order is null || invoice is null) return InvoiceBookingState.NotBooked;
        if (!holded.IsConfigured) return invoice.BookingState ?? InvoiceBookingState.NotBooked;

        var posted = new List<string>();
        try
        {
            var (state, note) = await RunAsync(order, invoice, posted, ct);
            return await FinishAsync(order, invoice, state, note, posted, actorUserId);
        }
        catch (HoldedApiException ex)
        {
            logger.LogWarning("Store invoice booking for order {OrderId} stopped on Holded: {Message}", orderId, ex.Message);
            return await FinishAsync(order, invoice, InvoiceBookingState.Stopped,
                $"Holded refused a booking step: {ex.Message}", posted, actorUserId);
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

        var document = await holdedWrite.GetSalesDocumentAsync(kind, invoice.HoldedDocId, ct);
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
        var document = await holdedWrite.GetSalesDocumentAsync(kind, invoice.HoldedDocId, ct);
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
                var (amount, created) = await StripeAmountAndCreatedAsync(p, ct);
                if (created is null)
                    return (lines, waiting, $"Stripe would not say when payment {p.StripePaymentIntentId} was charged.");
                if (amount != p.AmountEur)
                    return (lines, waiting, $"Stripe charged EUR {amount:0.00} for {p.StripePaymentIntentId}, "
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

    /// <summary>A charge's line is the charge's gross at its creation second; a refund's is the refund's.</summary>
    private async Task<(decimal Amount, Instant? Created)> StripeAmountAndCreatedAsync(Payment p, CancellationToken ct)
    {
        if (p.AmountEur < 0m) return (p.AmountEur, p.ReceivedAt);
        if (p.StripePaymentIntentId is not { } pi) return (p.AmountEur, null);
        var charge = await stripe.GetStoreChargeAsync(pi, ct);
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
