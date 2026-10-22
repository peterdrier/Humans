using NodaTime;

namespace Humans.Gate.Domain;

/// <summary>Append-only. The unique index on <see cref="AdmitDedupeKey"/> is the duplicate guard; it doesn't wait for a vendor sync.</summary>
internal sealed class GateScanEvent
{
    public Guid Id { get; init; }

    /// <summary>Server clock: authoritative.</summary>
    public Instant OccurredAt { get; init; }

    /// <summary>Settable only so account merge can re-point it.</summary>
    public Guid ScannedByUserId { get; set; }

    /// <summary>Normalized.</summary>
    public string Barcode { get; init; } = string.Empty;

    /// <summary>Null when the barcode matched no current-event ticket.</summary>
    public Guid? TicketAttendeeId { get; init; }

    /// <summary>Null when the ticket is unmatched. Settable only so account merge can re-point it.</summary>
    public Guid? GuestUserId { get; set; }

    public GateVerdict Verdict { get; init; }

    public string? LaneId { get; init; }

    /// <summary>Audit only: never trusted for the cutoff, which uses the server clock.</summary>
    public Instant? ClientScanAt { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// Set on override admits. The shared supervisor PIN authorizes but cannot attribute, so this holds the gate
    /// account. Settable so account merge can re-point it.
    /// </summary>
    public Guid? OverrideByUserId { get; set; }

    /// <summary>
    /// The normalized barcode for an admit, null otherwise. Its unique index makes the first admit win atomically
    /// across lanes; Postgres excludes nulls, so rejects never collide.
    /// </summary>
    public string? AdmitDedupeKey { get; init; }
}

