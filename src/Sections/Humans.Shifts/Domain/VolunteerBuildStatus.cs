using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>
/// Coordinator-only state for the Volunteer Tracking page. A row with no set-up date and no day-offs is the same
/// as no row.
/// </summary>
internal sealed class VolunteerBuildStatus
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public Guid EventSettingsId { get; set; }

    /// <summary>From this day on the heatmap shows CampSetup, never a gap.</summary>
    public LocalDate? BarrioSetupStartDate { get; set; }

    public string? Notes { get; set; }

    /// <summary>Who last changed <see cref="BarrioSetupStartDate"/>.</summary>
    public Guid? SetByUserId { get; set; }

    public Instant? SetAt { get; set; }

    /// <summary>
    /// Days in the build window the coordinator knows the volunteer is off-site; one entry per DayOffset, kept so
    /// by the app. See <see href="../Docs/2026-05-09-day-off-redesign-design.md"/>.
    /// </summary>
    public List<DayOffEntry> DayOffs { get; set; } = [];
}

internal sealed record DayOffEntry(
    int DayOffset,
    string? Reason,
    Guid MarkedByUserId,
    Instant MarkedAt);
