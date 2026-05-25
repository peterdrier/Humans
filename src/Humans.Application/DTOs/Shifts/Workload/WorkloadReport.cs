namespace Humans.Application.DTOs.Shifts.Workload;

/// <summary>
/// Site-wide workload aggregations sliced "three ways to slice the cake":
/// per-person, per-rota, per-department. Hours combine shift signups with
/// team role estimates (<c>TeamRoleDefinition.EstimatedHours</c>), mapped to
/// the role's <c>RolePeriod</c>. Per-rota stays shift-only — roles are
/// team-scoped, not rota-scoped.
/// </summary>
/// <param name="EventSettingsId">The event the rows describe.</param>
/// <param name="EventYear">Display label.</param>
/// <param name="ByPerson">Per-volunteer rows; one row per user with at least one signup or role assignment.</param>
/// <param name="ByRota">Per-rota roll-up: planned hours / slots vs filled.</param>
/// <param name="ByDepartment">Per-department roll-up: planned hours / slots vs filled.</param>
public record WorkloadReport(
    Guid EventSettingsId,
    int EventYear,
    IReadOnlyList<WorkloadByPersonRow> ByPerson,
    IReadOnlyList<WorkloadByRotaRow> ByRota,
    IReadOnlyList<WorkloadByDepartmentRow> ByDepartment);

/// <summary>
/// Per-person workload row. Confirmed shift hours are split by shift period
/// (Build/Event/Strike); role estimates are added to the column matching the
/// role's <c>RolePeriod</c>, with year-round roles in their own column. Pending
/// signups are reported as a count only — they don't inflate hours so a
/// coordinator can spot "lots queued, none approved" patterns without
/// false-positive burnout signals.
/// </summary>
public record WorkloadByPersonRow(
    Guid UserId,
    string DisplayName,
    int ConfirmedSignupCount,
    int PendingSignupCount,
    decimal YearRoundHours,
    decimal BuildHours,
    decimal EventHours,
    decimal StrikeHours)
{
    public decimal TotalHours => YearRoundHours + BuildHours + EventHours + StrikeHours;
}

/// <summary>
/// Per-rota roll-up: one row per rota in the event. Same shape as the per-department
/// row, one level deeper.
/// </summary>
public record WorkloadByRotaRow(
    Guid RotaId,
    string RotaName,
    Guid TeamId,
    string TeamName,
    int ShiftCount,
    int PlannedSlots,
    int FilledSlots,
    int PendingSignupCount,
    decimal PlannedHours,
    decimal FilledHours);

/// <summary>
/// Per-department roll-up: planned vs filled hours and slots across every
/// shift on rotas owned by the department. <see cref="PlannedHours"/> /
/// <see cref="FilledHours"/> also fold in the department's role estimates
/// (planned = estimate × slots, filled = estimate × assigned slots); slot
/// counts and coverage stay shift-only. A department with roles but no rotas
/// still gets a row. <see cref="TeamSlug"/> is the URL slug for linking to
/// <c>/Teams/{slug}/Shifts</c>.
/// </summary>
public record WorkloadByDepartmentRow(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    int RotaCount,
    int ShiftCount,
    int PlannedSlots,
    int FilledSlots,
    decimal PlannedHours,
    decimal FilledHours);
