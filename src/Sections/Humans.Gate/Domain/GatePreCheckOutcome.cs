namespace Humans.Gate.Domain;

/// <summary>Computed fresh on every scan, before the manual photo-ID check.</summary>
internal enum GatePreCheckOutcome
{
    /// <summary>Not found for the current event, or void/refunded. STOP.</summary>
    Invalid,

    /// <summary>Admitted locally or checked in at the vendor. STOP.</summary>
    Duplicate,

    /// <summary>AMBER, never a silent admit, while the general-entry cutoff is unset.</summary>
    CutoffNotConfigured,

    /// <summary>Before the cutoff with no Early Entry covering today. STOP.</summary>
    TooEarly,

    /// <summary>Before the cutoff and the ticket matches no member, so Early Entry can't be confirmed. AMBER, never TooEarly.</summary>
    EarlyEntryUnknown,

    /// <summary>General entry is open: on to the photo-ID check.</summary>
    NeedsIdCheck,

    /// <summary>Early Entry covers today: on to the photo-ID check, admitting early on Yes.</summary>
    NeedsIdCheckEarly,
}
