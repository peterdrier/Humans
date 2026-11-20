using System.Globalization;
using System.Net.Http.Json;
using Humans.Holded.Contracts;
using NodaTime;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

/// <summary><c>POST /{segment}/{id}/payments</c>, shared by purchase and sales documents. The POST answers no payment
/// id, so the id is the one new <c>payments_detail</c> entry matching amount, date and treasury, read before and
/// after.</summary>
internal static class HoldedDocumentPayment
{
    public static async Task<string> PayAsync(
        HoldedTransport transport, ILogger logger, string segment, string documentId,
        Func<CancellationToken, Task<IReadOnlyList<HoldedPaymentDetailDto>>> readPayments,
        decimal amount, string? treasuryId, LocalDate date, string? description, CancellationToken ct)
    {
        // A failed before-read throws with nothing posted. Not GET /payments: it stamps a payment dated D as
        // (D-1)T22:00Z.
        var before = await readPayments(ct);

        // v2 takes the amount as a decimal *string*; a JSON number is rejected.
        var payload = new
        {
            amount = amount.ToString("F2", CultureInfo.InvariantCulture),
            treasury_id = string.IsNullOrWhiteSpace(treasuryId) ? null : treasuryId,
            date = LocalDatePattern.Iso.Format(date),
            description,
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/{segment}/{documentId}/payments")
        { Content = JsonContent.Create(payload, options: OmitNulls) };

        // A 5xx can arrive after Holded saved the payment (production, 2026-11-05): only the document says which.
        HoldedTransientException? refused = null;
        try
        {
            using var resp = await transport.SendAsync(req, ct);
        }
        catch (HoldedTransientException ex)
        {
            refused = ex;
        }

        // From here the payment may be posted: throwing would read as "not paid" and the caller would pay again. The
        // sentinel names the document a human has to check.
        IReadOnlyList<HoldedPaymentDetailDto> after;
        try
        {
            after = await readPayments(ct);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex,
                "Holded {Outcome} a payment on {Segment} document {DocumentId} but the document could not be re-read for its id; recording it as unconfirmed.",
                refused is null ? "accepted" : "answered " + refused.Message + " to", segment, documentId);
            return UnconfirmedPaymentRefPrefix + documentId;
        }

        var known = before.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var matches = after
            .Where(p => !known.Contains(p.Id)
                        && p.Amount == amount
                        && p.Date == date
                        && (string.IsNullOrWhiteSpace(treasuryId)
                            || string.Equals(p.BankId, treasuryId, StringComparison.Ordinal)))
            .ToList();
        if (matches.Count == 1) return matches[0].Id;
        // Refused and nothing new on the document: it was not posted.
        if (refused is not null && after.All(p => known.Contains(p.Id)))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refused);

        logger.LogWarning(
            "Holded accepted a payment of {Amount} dated {Date} on {Segment} document {DocumentId} but payments_detail shows {Count} new matching entries (before: [{Before}], after: [{After}]); recording it as unconfirmed.",
            amount, date, segment, documentId, matches.Count,
            string.Join(", ", before.Select(Describe)), string.Join(", ", after.Select(Describe)));
        return UnconfirmedPaymentRefPrefix + documentId;

        static string Describe(HoldedPaymentDetailDto p) =>
            $"{p.Id} {p.Amount.ToString("F2", CultureInfo.InvariantCulture)} {LocalDatePattern.Iso.Format(p.Date)} {p.BankId}";
    }
}
