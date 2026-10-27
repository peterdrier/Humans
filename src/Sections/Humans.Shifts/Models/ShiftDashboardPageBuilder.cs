using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using NodaTime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Humans.Shifts.Models;

internal sealed record ShiftDashboardPageRequest(
    BurnSettingsInfo EventSettings,
    Guid? DepartmentId,
    Guid? RotaId,
    string? StartDate,
    string? EndDate,
    LocalDate? FilterStartDate,
    LocalDate? FilterEndDate,
    LocalDate? ActiveStart,
    LocalDate? ActiveEnd,
    TrendWindow TrendWindow,
    ShiftPeriod? Period,
    BuildSubPeriod? SubPeriod);

internal sealed class ShiftDashboardPageBuilder(
    IShiftManagement shiftManagement,
    IWebHostEnvironment environment,
    IClock clock)
{
    public async Task<ShiftDashboardViewModel> BuildAsync(ShiftDashboardPageRequest request, CancellationToken ct = default)
    {
        var es = request.EventSettings;

        var shifts = await shiftManagement.GetUrgentShiftsAsync(
            es.Id,
            limit: null,
            request.DepartmentId,
            request.ActiveStart,
            request.ActiveEnd,
            request.Period,
            request.SubPeriod,
            includeRestricted: true, ct: ct);
        var staffing = await shiftManagement.GetStaffingSnapshotAsync(es.Id, request.DepartmentId, request.Period, request.SubPeriod, ct);
        var overview = await shiftManagement.GetDashboardOverviewAsync(es.Id, request.Period, request.SubPeriod, ct);
        var coordinatorActivity = await shiftManagement.GetCoordinatorActivityAsync(es.Id, request.Period, request.SubPeriod, ct);
        var trends = await shiftManagement.GetDashboardTrendsAsync(es.Id, TrendWindow.All, request.Period, request.SubPeriod, ct);
        var dailyDeptStaffing = await shiftManagement.GetDailyDepartmentStaffingAsync(es.Id, request.Period, request.SubPeriod, ct);
        var shiftDurationBreakdown = await shiftManagement.GetShiftDurationBreakdownAsync(es.Id, request.Period, request.SubPeriod, ct);
        var coverageHeatmap = await shiftManagement.GetCoverageHeatmapAsync(es.Id, request.Period, request.SubPeriod, ct);
        var deptTuples = await shiftManagement.GetDepartmentsWithRotasAsync(es.Id, ct);

        return new ShiftDashboardViewModel
        {
            Shifts = shifts.ToList(),
            Departments = deptTuples
                .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName })
                .ToList(),
            SelectedDepartmentId = request.DepartmentId,
            SelectedRotaId = request.RotaId,
            SelectedStartDate = request.StartDate,
            SelectedEndDate = request.EndDate,
            SelectedPeriod = request.Period,
            SelectedSubPeriod = request.SubPeriod,
            FilterStartDate = request.FilterStartDate,
            FilterEndDate = request.FilterEndDate,
            EventSettings = es,
            StaffingData = staffing.StaffingData.ToList(),
            StaffingHours = staffing.StaffingHours.ToList(),
            Overview = overview,
            CoordinatorActivity = coordinatorActivity,
            Trends = trends,
            DailyDepartmentStaffing = dailyDeptStaffing,
            ShiftDurationBreakdown = shiftDurationBreakdown,
            CoverageHeatmap = coverageHeatmap,
            TrendWindow = request.TrendWindow,
            IsDevelopment = environment.IsDevelopment(),
            Countdown = BuildCountdown(es)
        };
    }

    private BuildDayCountdown BuildCountdown(BurnSettingsInfo eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId) ?? DateTimeZone.Utc;
        var todayLocal = clock.GetCurrentInstant().InZone(tz).Date;
        var (firstBuildDay, _) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Build, eventSettings);
        var daysToBuild = Period.Between(todayLocal, firstBuildDay, PeriodUnits.Days).Days;

        return new BuildDayCountdown(
            DaysToBuild: daysToBuild,
            FirstBuildDay: firstBuildDay,
            Weeks: Math.Abs(daysToBuild) / 7,
            RemainderDays: Math.Abs(daysToBuild) % 7);
    }
}
