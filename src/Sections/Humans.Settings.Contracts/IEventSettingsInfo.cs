using NodaTime;

namespace Humans.Settings.Contracts;

/// <summary>
/// The event's calendar shape — the read-only fields needed to resolve a
/// <c>DayOffset</c> into absolute instants and to classify it into a period or
/// build sub-period. Implemented by the Settings-owned EF entity
/// (<c>Humans.Settings.Domain.EventSettings</c>) and by its cross-section read
/// DTO (<see cref="EventSettingsInfo"/>), so domain helpers that only need the
/// calendar can accept either without the entity leaking across the section
/// boundary.
/// </summary>
/// <remarks>
/// Lives on the Contracts leaf — the bottom of the section's graph — so any
/// section can name it. Members are restricted to BCL and NodaTime types.
/// </remarks>
public interface IEventSettingsInfo
{
    /// <summary>IANA timezone ID (e.g. "Europe/Madrid").</summary>
    string TimeZoneId { get; }

    /// <summary>The date gates open — DayOffset 0.</summary>
    LocalDate GateOpeningDate { get; }

    /// <summary>Offset for the last event day (inclusive). Strike starts at EventEndOffset + 1.</summary>
    int EventEndOffset { get; }

    /// <summary>Inclusive start day of the "First crew" build sub-period.</summary>
    int FirstCrewStartOffset { get; }

    /// <summary>Inclusive start day of the "Set-up week" build sub-period.</summary>
    int SetupWeekStartOffset { get; }

    /// <summary>Inclusive start day of the "Pre-event week" build sub-period.</summary>
    int PreEventWeekStartOffset { get; }

    /// <summary>Inclusive start day of the "Finishing weekend" build sub-period.</summary>
    int FinishingWeekendStartOffset { get; }

    /// <summary>After this instant, non-privileged users cannot sign up for or bail build shifts.</summary>
    Instant? EarlyEntryClose { get; }

    /// <summary>
    /// True once <see cref="EarlyEntryClose"/> has passed (<paramref name="now"/> ≥ close).
    /// The single home for the early-entry-closed clock rule: the server gates
    /// (ShiftSignupService) and the browse UI both call this, ANDing it with the
    /// viewer's privilege and the shift's <c>Shift.IsEarlyEntry</c> at each call site.
    /// </summary>
    /// <remarks>
    /// <c>sealed</c> (non-virtual) on purpose: implementers must not be able to
    /// override the clock rule, and it lets <c>EventSettings</c> forward its
    /// same-named public method here without the call dispatching back into
    /// itself.
    /// </remarks>
    public sealed bool IsEarlyEntryClosed(Instant now) =>
        EarlyEntryClose.HasValue && now >= EarlyEntryClose.Value;

    /// <summary>
    /// True when early-entry (build) sign-ups are closed for a viewer with the given
    /// privilege at <paramref name="now"/> — the close has passed and the viewer is
    /// not privileged. This is the page-level eligibility the browse/onboarding UI
    /// surfaces (combined per-shift with <c>Shift.IsEarlyEntry</c> in the view);
    /// the server gates in ShiftSignupService compose <see cref="IsEarlyEntryClosed"/>
    /// with per-team privilege directly instead.
    /// </summary>
    /// <remarks><c>sealed</c> for the same reason as <see cref="IsEarlyEntryClosed"/>.</remarks>
    public sealed bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        !isPrivileged && IsEarlyEntryClosed(now);
}
