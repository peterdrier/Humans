using NodaTime;

namespace Humans.Gate.Domain;

/// <summary>
/// One append-only record per gate scan, owned by the Gate section
/// (<c>gate_scan_events</c>). It is both the audit trail ("who admitted which
/// ticket, when") and the source for leaderboards, and — via a partial unique
/// index on <see cref="Barcode"/> for admit verdicts — the authoritative,
/// instant duplicate guard that does not depend on the next vendor sync.
///
/// Cross-section links (<see cref="ScannedByUserId"/>, <see cref="GuestUserId"/>,
/// <see cref="TicketAttendeeId"/>) are bare Guid columns with no navigation
/// properties or FK constraints, per the no-cross-section-EF-joins rule.
/// </summary>
internal sealed class GateScanEvent
{
    public Guid Id { get; init; }

    /// <summary>Server-clock instant the scan was recorded — authoritative for audit and leaderboards.</summary>
    public Instant OccurredAt { get; init; }

    /// <summary>The gate staffer who held the scanning session (bare cross-section FK to a Humans user). Settable only so account-merge can re-point it.</summary>
    public Guid ScannedByUserId { get; set; }

    /// <summary>The Ticket Tailor issued-ticket barcode that was scanned (normalized).</summary>
    public string Barcode { get; init; } = string.Empty;

    /// <summary>The matched local ticket attendee, or null when the barcode matched no current-event ticket. Bare cross-section FK.</summary>
    public Guid? TicketAttendeeId { get; init; }

    /// <summary>The matched Human (ticket holder), or null when the ticket is unmatched. Bare cross-section FK. Settable only so account-merge can re-point it.</summary>
    public Guid? GuestUserId { get; set; }

    /// <summary>The recorded outcome of this scan.</summary>
    public GateVerdict Verdict { get; init; }

    /// <summary>Optional gate/lane label, for multi-lane throughput attribution.</summary>
    public string? LaneId { get; init; }

    /// <summary>Device-reported scan time. Audit only — never trusted for the cutoff decision (which uses the server clock).</summary>
    public Instant? ClientScanAt { get; init; }

    /// <summary>Optional free-text note (e.g. supervisor context on an unresolved escalation).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The supervisor who authorized an override admit (their personal-PIN identity),
    /// for <see cref="GateVerdict.AdmittedEarlyOverride"/> and the child-with-adult
    /// waiver. <c>null</c> for ordinary scans. A bare user-id FK (no navigation), per
    /// the cross-section linkage rule — the supervisor is a Humans user, stitched via
    /// services. This is the audit trail for "who let this person in early".
    /// <c>set</c> (not <c>init</c>) so account-merge reassignment can re-point it, like <see cref="GuestUserId"/>.
    /// </summary>
    public Guid? OverrideByUserId { get; set; }

    /// <summary>
    /// Dedupe key: the normalized barcode for an admit verdict, <c>null</c> otherwise.
    /// A unique index on this column (Postgres excludes nulls) makes the very first
    /// admit for a barcode win atomically across all lanes — a concurrent second
    /// admit insert violates the constraint and is reported as a duplicate. Reject
    /// events leave this null so they never collide.
    /// </summary>
    public string? AdmitDedupeKey { get; init; }
}

