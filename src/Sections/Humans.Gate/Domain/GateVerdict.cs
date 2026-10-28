namespace Humans.Gate.Domain;

/// <summary>The durable audit and leaderboard value, unlike <see cref="GatePreCheckOutcome"/>.</summary>
internal enum GateVerdict
{
    Admitted,

    /// <summary>Under an Early Entry grant, before the general-entry cutoff.</summary>
    AdmittedEarly,

    /// <summary>Photo ID waived for a child with an accompanying adult, authorized by the supervisor override PIN.</summary>
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

    /// <summary>No admit or reject recorded: escalated to a supervisor, or the cutoff is unset.</summary>
    Unresolved,
}
