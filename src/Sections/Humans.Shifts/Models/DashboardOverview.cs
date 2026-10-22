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
/// Set-up and Strike only: Event days plan differently. Counts Confirmed signups only.
/// </summary>
internal sealed record DailyDepartmentStaffing(
    LocalDate Date,
    string DateLabel,
    IReadOnlyList<DepartmentDayCount> Departments);

internal sealed record DepartmentDayCount(string DepartmentName, int ConfirmedCount);

/// <summary>All-day shifts share one bucket whatever their nominal duration.</summary>
internal sealed record ShiftDurationBreakdownRow(
    bool IsAllDay,
    int DurationHours,
    int TotalSlots,
    int FilledSlots);

/// <summary>Each cell counts slot fill for shifts overlapping that calendar day.</summary>
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
