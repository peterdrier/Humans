using Humans.Domain.Enums;
using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Models;

internal sealed record DashboardOverview(
    int TotalShifts,
    int FilledShifts,
    int TotalSlots,
    int FilledSlots,
    PeriodBreakdown PeriodFillRates,
    int TicketHolderCount,
    int TicketHoldersEngaged,
    int NonTicketSignups,
    int StalePendingCount,
    IReadOnlyList<DepartmentStaffingRow> Departments);

internal sealed record PeriodBreakdown(double BuildPct, double EventPct, double StrikePct);

internal sealed record DepartmentStaffingRow(
    Guid DepartmentId,
    string DepartmentName,
    string? DepartmentSlug,
    int TotalShifts,
    int FilledShifts,
    int TotalSlots,
    int FilledSlots,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike,
    IReadOnlyList<SubgroupStaffingRow> Subgroups);

internal sealed record SubgroupStaffingRow(
    Guid? TeamId,
    string Name,
    string? Slug,
    bool IsDirect,
    int TotalShifts,
    int FilledShifts,
    int TotalSlots,
    int FilledSlots,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike);

internal sealed record PeriodStaffing(int Total, int Filled, int TotalSlots, int FilledSlots, int SlotsRemaining);

internal sealed record CoordinatorActivityRow(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<CoordinatorLogin> Coordinators,
    int PendingSignupCount,
    int AggregatePendingCount,
    IReadOnlyList<CoordinatorActivityRow> Subgroups);

internal sealed record CoordinatorLogin(Guid UserId, Instant? LastLoginAt);

internal sealed record DashboardTrendPoint(
    LocalDate Date,
    int NewSignups,
    int NewTicketSales,
    int DistinctLogins);

/// <summary>
/// One bar on the "people on site per day, stacked by department" chart. Only
/// populated for Set-up and Strike periods — Event day-over-day mix has a
/// different planning flow so the dashboard deliberately omits it there.
/// Counts are <c>Confirmed</c> signups only (pending/cancelled are excluded).
/// </summary>
internal sealed record DailyDepartmentStaffing(
    LocalDate Date,
    string DateLabel,
    IReadOnlyList<DepartmentDayCount> Departments);

internal sealed record DepartmentDayCount(string DepartmentName, int ConfirmedCount);

/// <summary>
/// A row in the "shift duration mix" table. One row per distinct duration bucket
/// (full-day shifts share a bucket regardless of nominal duration). Scope is the
/// selected period — Build, Event, or Strike.
/// </summary>
internal sealed record ShiftDurationBreakdownRow(
    bool IsAllDay,
    int DurationHours,
    int TotalSlots,
    int FilledSlots);

/// <summary>
/// Coverage heatmap: one row per rota, one cell per day in the selected scope.
/// Each cell reports slot fill for shifts that overlap that calendar day, so
/// coordinators can spot day-of-week patterns across the whole event.
/// </summary>
internal sealed record CoverageHeatmap(
    IReadOnlyList<CoverageHeatmapDay> Days,
    IReadOnlyList<CoverageHeatmapRotaRow> Rotas);

internal sealed record CoverageHeatmapDay(int DayOffset, LocalDate Date, string DateLabel, ShiftPeriod Period);

internal sealed record CoverageHeatmapRotaRow(
    Guid RotaId,
    string RotaName,
    string DepartmentName,
    IReadOnlyList<CoverageHeatmapCell> Cells);

internal sealed record CoverageHeatmapCell(int DayOffset, int TotalSlots, int FilledSlots);
