using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>One mirrored purchase document. <paramref name="Notes"/> is null until the sync has read them (the list
/// endpoint does not return notes). <paramref name="Tags"/> are the document's and its lines' tags as Holded spells
/// them; <paramref name="BookedAccountId"/> is the first line's account id.</summary>
public sealed record HoldedPurchaseDocInfo(
    string Id,
    string DocNumber,
    string ContactName,
    string? ContactId,
    string? Description,
    LocalDate Date,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    string Currency,
    bool IsApproved,
    string? Notes,
    IReadOnlyList<string> Tags,
    string? BookedAccountId,
    string HoldedUrl);

/// <summary>The purchase-document mirror and the state of the sync that fills it. <paramref name="LastSyncAt"/> is
/// the last <b>successful</b> sync (null when none has completed); a failed one sets <paramref name="Status"/> to
/// <c>Error</c> and leaves it. <paramref name="StatusChangedAt"/> dates the current status.</summary>
public sealed record HoldedPurchaseDocMirror(
    Instant? LastSyncAt,
    string Status,
    string? LastError,
    int LastCount,
    Instant? StatusChangedAt,
    IReadOnlyList<HoldedPurchaseDocInfo> Docs);
