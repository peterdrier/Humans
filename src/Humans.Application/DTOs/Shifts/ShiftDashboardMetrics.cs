namespace Humans.Application.DTOs;

public record ShiftDashboardMetrics(
    DashboardOverview Overview,
    IReadOnlyList<CoordinatorActivityRow> CoordinatorActivity,
    IReadOnlyList<DashboardTrendPoint> Trends,
    IReadOnlyList<DailyDepartmentStaffing> DailyDepartmentStaffing,
    IReadOnlyList<ShiftDurationBreakdownRow> ShiftDurationBreakdown,
    CoverageHeatmap CoverageHeatmap);
