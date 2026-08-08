using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// The burn's calendar shape — the read-only fields needed to resolve a
/// <c>DayOffset</c> into absolute instants and to classify it into a period or
/// build sub-period. Implemented by both the Shifts-owned EF entity
/// (<c>Humans.Domain.Entities.EventSettings</c>) and its cross-section read DTO
/// (<see cref="BurnSettingsInfo"/>), so domain helpers that only need the
/// calendar can accept either without the entity leaking across the section
/// boundary.
/// </summary>
/// <remarks>
/// Lives in <c>Humans.Interfaces</c> — the bottom of the dependency graph —
/// precisely so <c>Humans.Domain</c> can reference it. Members are restricted to
/// BCL and NodaTime types; a Humans type here would drag a project reference
/// into <c>Humans.Interfaces</c> and invert the graph.
/// </remarks>
public interface IBurnSettingsInfo
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

    /// <summary>Whether the shift browsing system is open to regular volunteers.</summary>
    bool IsShiftBrowsingOpen { get; }
}
