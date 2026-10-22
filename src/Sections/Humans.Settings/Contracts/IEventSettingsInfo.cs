using NodaTime;

namespace Humans.Settings.Contracts;

/// <summary>
/// The calendar shape, implemented by both the Settings entity and <see cref="EventSettingsInfo"/>, so calendar
/// helpers accept either without the entity leaving the section. Members use BCL and NodaTime types only.
/// </summary>
public interface IEventSettingsInfo
{
    /// <summary>IANA timezone ID.</summary>
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
    /// True once <paramref name="now"/> ≥ <see cref="EarlyEntryClose"/>. Callers AND this with the viewer's privilege
    /// and <c>Shift.IsEarlyEntry</c>.
    /// </summary>
    /// <remarks>
    /// <c>sealed</c> so implementers cannot override the clock rule, and so <c>EventSettingsInfo</c>'s same-named method
    /// can forward here without dispatching back into itself.
    /// </remarks>
    public sealed bool IsEarlyEntryClosed(Instant now) =>
        EarlyEntryClose.HasValue && now >= EarlyEntryClose.Value;

    /// <summary>
    /// Page-level eligibility: the close has passed and the viewer is not privileged. Per-team server gates compose
    /// <see cref="IsEarlyEntryClosed"/> with privilege directly instead.
    /// </summary>
    /// <remarks><c>sealed</c> for the same reason as <see cref="IsEarlyEntryClosed"/>.</remarks>
    public sealed bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        !isPrivileged && IsEarlyEntryClosed(now);
}
