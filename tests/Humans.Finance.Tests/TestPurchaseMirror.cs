using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Tests;

/// <summary>Purchase-document mirrors built from list-shaped docs, for the reads that moved off the live list.</summary>
internal static class TestPurchaseMirror
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public static HoldedPurchaseDocMirror Of(IEnumerable<HoldedPurchaseDocListItemDto> docs) =>
        new(Instant.FromUtc(2026, 1, 1, 0, 0), "Idle", null, 0, null, docs.Select(d => new HoldedPurchaseDocInfo(
                d.Id, d.DocNumber, d.ContactName, d.ContactId, d.Description, d.Date.InZone(Madrid).Date,
                d.Subtotal, d.Tax, d.Total, d.Currency, d.IsDraft == false, null, d.Tags, null, "", d.PaymentsPending))
            .ToList());

    public static HoldedPurchaseDocMirror NeverSynced { get; } = new(null, "Idle", null, 0, null, []);
}
