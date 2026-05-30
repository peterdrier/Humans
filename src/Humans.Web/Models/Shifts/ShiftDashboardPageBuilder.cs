using Humans.Application.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

public sealed record ShiftDashboardPageRequest(
    EventSettings EventSettings,
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

public sealed class ShiftDashboardPageBuilder(
    IShiftManagementService shiftManagement,
    IWebHostEnvironment environment,
    IClock clock)
{
    public async Task<ShiftDashboardViewModel> BuildAsync(ShiftDashboardPageRequest request)
    {
        var es = request.EventSettings;

        var shifts = await shiftManagement.GetUrgentShiftsAsync(
            es.Id,
            limit: null,
            request.DepartmentId,
            request.ActiveStart,
            request.ActiveEnd,
            request.Period,
            request.SubPeriod);
        var staffing = await shiftManagement.GetStaffingSnapshotAsync(es.Id, request.DepartmentId, request.Period, request.SubPeriod);
        var overview = await shiftManagement.GetDashboardOverviewAsync(es.Id, request.Period, request.SubPeriod);
        var coordinatorActivity = await shiftManagement.GetCoordinatorActivityAsync(es.Id, request.Period, request.SubPeriod);
        var trends = await shiftManagement.GetDashboardTrendsAsync(es.Id, TrendWindow.All, request.Period, request.SubPeriod);
        var dailyDeptStaffing = await shiftManagement.GetDailyDepartmentStaffingAsync(es.Id, request.Period, request.SubPeriod);
        var shiftDurationBreakdown = await shiftManagement.GetShiftDurationBreakdownAsync(es.Id, request.Period, request.SubPeriod);
        var coverageHeatmap = await shiftManagement.GetCoverageHeatmapAsync(es.Id, request.Period, request.SubPeriod);
        var deptTuples = await shiftManagement.GetDepartmentsWithRotasAsync(es.Id);

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

    private BuildDayCountdown BuildCountdown(EventSettings eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId) ?? DateTimeZone.Utc;
        var todayLocal = clock.GetCurrentInstant().InZone(tz).Date;
        var firstBuildDay = eventSettings.GateOpeningDate.PlusDays(eventSettings.BuildStartOffset);
        var daysToBuild = Period.Between(todayLocal, firstBuildDay, PeriodUnits.Days).Days;

        return new BuildDayCountdown(
            DaysToBuild: daysToBuild,
            FirstBuildDay: firstBuildDay,
            Weeks: Math.Abs(daysToBuild) / 7,
            RemainderDays: Math.Abs(daysToBuild) % 7);
    }
}
