using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>Written before a Pleo entry's purchase is created, so the create is never sent twice: a claimed entry
/// whose <c>Pleo-{Id}</c> purchase Holded does not list is shown as "check in Holded", never created again. Holds no
/// Holded id; the purchase is found by its number.</summary>
internal sealed class PleoBookingClaim
{
    public string PleoEntryId { get; init; } = "";
    public Instant ClaimedAt { get; init; }
}

/// <summary>A Pleo receipt uploaded to its entry's Holded purchase. Holded's purchase read reports no attachments, so
/// this is how a receipt is attached once. Written when the upload returns, or when Holded did not answer (it may
/// have landed; never sent again).</summary>
internal sealed class PleoReceiptUpload
{
    public string PleoEntryId { get; init; } = "";
    public string ReceiptId { get; init; } = "";
    public Instant UploadedAt { get; init; }
    /// <summary>False when Holded did not answer the upload: check the purchase in Holded.</summary>
    public bool Confirmed { get; init; }
}
