namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// Hours combine shift signups with team role estimates mapped to each role's <c>RolePeriod</c>. The per-rota
/// slice stays shift-only: roles are team-scoped, not rota-scoped.
/// </summary>
internal sealed record WorkloadReport(
    Guid EventSettingsId,
    int EventYear,
    IReadOnlyList<WorkloadByPersonRow> ByPerson,
    IReadOnlyList<WorkloadByRotaRow> ByRota,
    IReadOnlyList<WorkloadByDepartmentRow> ByDepartment);

/// <summary>
/// Year-round roles get their own column. Pending signups are a count only, so queued work raises no false burnout
/// signal.
/// </summary>
internal sealed record WorkloadByPersonRow(
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

internal sealed record WorkloadByRotaRow(
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
/// Hours fold in the department's role estimates (planned = estimate × slots, filled = estimate × assigned); slot
/// counts and coverage stay shift-only. A department with roles but no rotas still gets a row.
/// </summary>
internal sealed record WorkloadByDepartmentRow(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    int RotaCount,
    int ShiftCount,
    int PlannedSlots,
    int FilledSlots,
    decimal PlannedHours,
    decimal FilledHours);
