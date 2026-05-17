using NodaTime;

namespace Humans.Application.DTOs.Shifts.Workload;

/// <summary>
/// Site-wide workload aggregations sliced "three ways to slice the cake":
/// per-person, per-shift, per-department. Counts and hours derive from
/// shift signups only — role hours are deferred until role data carries an
/// EstimatedHours field (nobodies-collective/Humans#734 follow-up).
/// </summary>
/// <param name="EventSettingsId">The event the rows describe.</param>
/// <param name="EventYear">Display label.</param>
/// <param name="ByPerson">Per-volunteer rows; one row per user with at least one Pending or Confirmed signup.</param>
/// <param name="ByShift">Per-shift rows; one row per shift in the event (visible to volunteers, including admin-only).</param>
/// <param name="ByDepartment">Per-department roll-up: planned hours / slots vs filled.</param>
public record WorkloadReport(
    Guid EventSettingsId,
    int EventYear,
    IReadOnlyList<WorkloadByPersonRow> ByPerson,
    IReadOnlyList<WorkloadByShiftRow> ByShift,
    IReadOnlyList<WorkloadByDepartmentRow> ByDepartment);

/// <summary>
/// Per-person workload row. Hours are Confirmed-signup hours, in the event-local
/// time zone. Pending signups are reported separately so a coordinator can spot
/// "lots queued, none approved" patterns without inflating the burnout signal.
/// </summary>
public record WorkloadByPersonRow(
    Guid UserId,
    string DisplayName,
    int ConfirmedSignupCount,
    int PendingSignupCount,
    decimal ConfirmedHours);

/// <summary>
/// Per-shift row.
/// </summary>
public record WorkloadByShiftRow(
    Guid ShiftId,
    Guid RotaId,
    string RotaName,
    Guid TeamId,
    string TeamName,
    int DayOffset,
    LocalDate Date,
    bool IsAllDay,
    LocalTime StartTime,
    decimal DurationHours,
    int MaxVolunteers,
    int ConfirmedCount,
    int PendingCount);

/// <summary>
/// Per-department roll-up: planned vs filled hours and slots across every visible
/// shift on rotas owned by the department.
/// </summary>
public record WorkloadByDepartmentRow(
    Guid TeamId,
    string TeamName,
    int RotaCount,
    int ShiftCount,
    int PlannedSlots,
    int FilledSlots,
    decimal PlannedHours,
    decimal FilledHours);
