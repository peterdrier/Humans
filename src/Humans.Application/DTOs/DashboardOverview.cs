using NodaTime;

namespace Humans.Application.DTOs;

public record DashboardOverview(
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

public record PeriodBreakdown(double BuildPct, double EventPct, double StrikePct);

public record DepartmentStaffingRow(
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

public record SubgroupStaffingRow(
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

public record PeriodStaffing(int Total, int Filled, int TotalSlots, int FilledSlots, int SlotsRemaining);

public record CoordinatorActivityRow(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<CoordinatorLogin> Coordinators,
    int PendingSignupCount,
    int AggregatePendingCount,
    IReadOnlyList<CoordinatorActivityRow> Subgroups);

public record CoordinatorLogin(Guid UserId, string DisplayName, Instant? LastLoginAt);

public record DashboardTrendPoint(
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
public record DailyDepartmentStaffing(
    LocalDate Date,
    string DateLabel,
    IReadOnlyList<DepartmentDayCount> Departments);

public record DepartmentDayCount(string DepartmentName, int ConfirmedCount);

/// <summary>
/// A row in the "shift duration mix" table. One row per distinct duration bucket
/// (full-day shifts share a bucket regardless of nominal duration). Scope is the
/// selected period — Build, Event, or Strike.
/// </summary>
public record ShiftDurationBreakdownRow(
    bool IsAllDay,
    int DurationHours,
    int ShiftCount,
    int TotalSlots,
    int FilledSlots);

/// <summary>
/// Coverage heatmap: one row per rota, one cell per day in the selected scope.
/// Each cell reports slot fill for shifts that overlap that calendar day, so
/// coordinators can spot day-of-week patterns across the whole event.
/// </summary>
public record CoverageHeatmap(
    IReadOnlyList<CoverageHeatmapDay> Days,
    IReadOnlyList<CoverageHeatmapRotaRow> Rotas);

public record CoverageHeatmapDay(int DayOffset, LocalDate Date, string DateLabel, string PeriodLabel);

public record CoverageHeatmapRotaRow(
    Guid RotaId,
    string RotaName,
    string DepartmentName,
    IReadOnlyList<CoverageHeatmapCell> Cells);

public record CoverageHeatmapCell(int DayOffset, int TotalSlots, int FilledSlots);
