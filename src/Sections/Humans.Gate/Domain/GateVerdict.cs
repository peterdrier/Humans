namespace Humans.Gate.Domain;

/// <summary>The durable audit and leaderboard value, unlike <see cref="GatePreCheckOutcome"/>.</summary>
internal enum GateVerdict
{
    Admitted,

    /// <summary>Under an Early Entry grant, before the general-entry cutoff.</summary>
    AdmittedEarly,

    /// <summary>The photo-ID step is waived below the configured age threshold.</summary>
    AdmittedChildWithAdult,

    /// <summary>Supervisor override despite a too-early pre-check; recorded on <see cref="GateScanEvent.OverrideByUserId"/>.</summary>
    AdmittedEarlyOverride,

    /// <summary>Not found for the current event, or void/refunded/cancelled.</summary>
    RejectedInvalid,

    /// <summary>Admitted locally before, or already checked in at the vendor.</summary>
    RejectedDuplicate,

    RejectedTooEarly,

    /// <summary>The agent tapped No: the photo ID doesn't match the ticket name.</summary>
    RejectedNameMismatch,

    /// <summary>Escalated to a supervisor with no recorded resolution.</summary>
    Unresolved,
}
