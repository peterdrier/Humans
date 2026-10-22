using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Implemented by the Shifts entity and by <see cref="BurnSettingsInfo"/>, so calendar helpers take either without
/// the entity leaking across the boundary.
/// </summary>
public interface IBurnSettingsInfo
{
    /// <summary>IANA, e.g. "Europe/Madrid".</summary>
    string TimeZoneId { get; }

    /// <summary>DayOffset 0.</summary>
    LocalDate GateOpeningDate { get; }

    /// <summary>Inclusive; strike starts the day after.</summary>
    int EventEndOffset { get; }

    /// <summary>Each sub-period offset is the inclusive start of its window.</summary>
    int FirstCrewStartOffset { get; }

    int SetupWeekStartOffset { get; }

    int PreEventWeekStartOffset { get; }

    int FinishingWeekendStartOffset { get; }

    /// <summary>After this, non-privileged users cannot sign up for or bail build shifts.</summary>
    Instant? EarlyEntryClose { get; }

    bool IsShiftBrowsingOpen { get; }

    /// <summary>
    /// The one home of the early-entry clock rule; callers AND it with privilege and <c>Shift.IsEarlyEntry</c>.
    /// Sealed so no implementer can override it, and so the entity's forwarding method can't recurse into itself.
    /// </summary>
    public sealed bool IsEarlyEntryClosed(Instant now) =>
        EarlyEntryClose.HasValue && now >= EarlyEntryClose.Value;

    /// <summary>
    /// Page-level eligibility for the browse and onboarding UI. The server gates compose
    /// <see cref="IsEarlyEntryClosed"/> with per-team privilege instead. Sealed for the same reason.
    /// </summary>
    public sealed bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        !isPrivileged && IsEarlyEntryClosed(now);
}
