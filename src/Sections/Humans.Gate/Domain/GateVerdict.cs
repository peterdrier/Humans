namespace Humans.Gate.Domain;

/// <summary>
/// The recorded outcome of a single gate scan, stored on
/// <see cref="Humans.Domain.Entities.GateScanEvent"/>. This is the durable
/// audit/leaderboard value — distinct from the pre-ID-check computation
/// (<see cref="GatePreCheckOutcome"/>) which only decides what the gate agent
/// is asked to do next.
/// </summary>
internal enum GateVerdict
{
    /// <summary>Valid ticket, ID confirmed by the agent, general entry open — admitted.</summary>
    Admitted,

    /// <summary>Valid ticket, ID confirmed, admitted under an Early Entry grant before the general-entry cutoff.</summary>
    AdmittedEarly,

    /// <summary>Minor admitted accompanied by a named adult; the photo-ID step is waived below the configured age threshold.</summary>
    AdmittedChildWithAdult,

    /// <summary>Admitted by a named supervisor override despite a too-early pre-check (e.g. the holder has an Early Entry grant for a later day). The authorizing supervisor is recorded on <see cref="Humans.Domain.Entities.GateScanEvent.OverrideByUserId"/>.</summary>
    AdmittedEarlyOverride,

    /// <summary>Ticket not found for the current event, or void/refunded/cancelled.</summary>
    RejectedInvalid,

    /// <summary>Ticket already used — a prior admit exists locally or the vendor already marked it checked in.</summary>
    RejectedDuplicate,

    /// <summary>Scanned before the general-entry cutoff and the holder has no Early Entry grant covering today.</summary>
    RejectedTooEarly,

    /// <summary>The agent tapped "No" on the ID check — the photo ID does not match the ticket name.</summary>
    RejectedNameMismatch,

    /// <summary>The verdict could not be decided automatically (e.g. Early Entry unknown for an unmatched ticket) and was escalated to a supervisor without a recorded resolution.</summary>
    Unresolved,
}
