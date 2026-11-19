using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Holded.Contracts;
using NodaTime;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedTreasuryClient(HoldedTransport transport, ILogger<HoldedClient> logger)
{
    public async Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null)
    {
        const int pageSafetyCap = 50; // far above a small nonprofit's line volume for a 90-day window
        // end_date is exclusive on the live API — send to+1, same rule as ListLedgerEntriesAsync.
        var query =
            $"/api/v2/treasury/accounts/{Uri.EscapeDataString(treasuryAccountId)}/bank-movements" +
            $"?start_date={LocalDatePattern.Iso.Format(from)}" +
            $"&end_date={LocalDatePattern.Iso.Format(to.PlusDays(1))}&limit=200";

        var items = await transport.GetPagedAsync(query, pageSafetyCap, ct);
        try
        {
            var movements = new List<HoldedBankMovementDto>();
            var skipped = 0;
            foreach (var n in items)
            {
                LocalDate date;
                try
                {
                    date = ParseBankMovementDate(n);
                }
                catch (Exception ex) when (ex is HoldedPermanentException or InvalidOperationException
                    or FormatException)
                {
                    // A line with no readable date can't be placed in the window. Skipping is safe only if it can
                    // never be a SEPA candidate: Finance's rival checks count every pending outgoing line, so dropping
                    // one would let an ambiguous booking through. Those fail the page unless the caller named the line
                    // as already surfaced.
                    if (ReadRequiredDecimalV2(Prop(n, "amount"), "amount") < 0m
                        && string.Equals(ReadRequiredString(Prop(n, "status"), "status"), "pending",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var id = Prop(n, "id")?.GetValue<string>();
                        if (id is null || skipUnreadableDateIds?.Contains(id) != true)
                            throw new HoldedPermanentException(
                                $"Holded bank movement '{id}' in account "
                                + $"{treasuryAccountId} is a pending outgoing line with no readable date.",
                                ex, id);
                        logger.LogWarning(
                            "Skipping Holded bank movement {MovementId} in account {AccountId}: a pending "
                            + "outgoing line with no readable date, skipped at the caller's request.",
                            id, treasuryAccountId);
                        continue;
                    }
                    if (skipped == 0)
                        logger.LogWarning("Unreadable Holded bank movement date; skipping the line.");
                    skipped++;
                    continue;
                }

                if (date < from || date > to) continue;

                movements.Add(new HoldedBankMovementDto
                {
                    Id = ReadRequiredString(Prop(n, "id"), "id"),
                    Date = date,
                    BookedAt = ParseBookedAt(n),
                    Amount = ReadRequiredDecimalV2(Prop(n, "amount"), "amount"),
                    Description = Prop(n, "description")?.GetValue<string>(),
                    // Never defaulted: "pending" is the one status the SEPA sweep reads as bookable, so inventing it
                    // could turn a settled line bookable. Any other present value already fails closed.
                    Status = ReadRequiredString(Prop(n, "status"), "status").ToLowerInvariant(),
                });
            }

            if (skipped > 0)
                logger.LogWarning(
                    "Skipped {Skipped} Holded bank movement(s) with an unreadable date in account {AccountId}.",
                    skipped, treasuryAccountId);
            return movements;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // A manufactured line would book against a movement that doesn't exist, so an unreadable
            // id/amount/status fails the page.
            throw new HoldedPermanentException(
                $"Holded bank-movements {from}..{to} for account {treasuryAccountId} could not be read.",
                ex);
        }
    }

    public async Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId,
        IReadOnlyList<HoldedReconcileDocumentRef> documents, CancellationToken ct = default)
    {
        var payload = new
        {
            documents = documents.Select(d => new
            {
                document_id = d.DocumentId,
                document_type = d.DocumentType,
            }),
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/treasury/accounts/{Uri.EscapeDataString(treasuryAccountId)}" +
            $"/bank-movements/{Uri.EscapeDataString(movementId)}/reconcile")
        { Content = JsonContent.Create(payload, options: OmitNulls) };

        using var resp = await transport.SendAsync(req, ct);
    }

    /// <summary><c>POST /payments</c>: money in (<c>collection</c>) or out (<c>payment</c>) on a contact, outside any
    /// document. Returns the payment's id, or <c>unconfirmed:payment</c> when Holded accepted it without a readable
    /// one; it is posted either way.</summary>
    public async Task<string> CreatePaymentAsync(
        HoldedTreasuryPaymentKind kind, string contactId, string treasuryId, decimal amount, LocalDate date,
        string? description, CancellationToken ct = default)
    {
        var payload = new
        {
            type = kind == HoldedTreasuryPaymentKind.Collection ? "collection" : "payment",
            contact_id = contactId,
            bank_account_id = treasuryId,
            // v2 takes amounts as decimal strings, as on document payments.
            amount = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            date = LocalDatePattern.Iso.Format(date),
            description,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/payments")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        using var resp = await transport.SendAsync(req, ct);
        try
        {
            var id = Prop(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)), "id")?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("Holded accepted a {Kind} of {Amount} on contact {ContactId} but its id could not be read: {Error}",
                kind, amount, contactId, ex.Message);
        }
        return UnconfirmedPaymentRefPrefix + "payment";
    }

    /// <summary>A bank line has no <c>date</c> key, only <c>booking_date</c> and <c>value_date</c> (ISO 8601 with an
    /// offset). Booking is what Holded stamps on the reconciled payment and entry, so it is the date; value is the
    /// fallback. The date part is taken as given, never shifted to Madrid: a midnight-UTC Sabadell line would move
    /// a day. Throws <c>HoldedPermanentException</c> when neither parses.</summary>
    private static Instant? ParseBookedAt(JsonNode line)
    {
        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(Prop(line, "booking_date")?.GetValue<string>() ?? "");
        return parsed.Success ? parsed.Value.ToInstant() : null;
    }

    private static LocalDate ParseBankMovementDate(JsonNode line)
    {
        var s = Prop(line, "booking_date")?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(s)) s = Prop(line, "value_date")?.GetValue<string>() ?? "";

        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(s);
        if (parsed.Success) return parsed.Value.Date;

        throw new HoldedPermanentException($"Holded bank movement date '{s}' could not be parsed.");
    }
}
