using Humans.Domain.Entities;

namespace Humans.Web.Models;

public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int ActiveHumans,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    int ErrorsLast24h,
    int FailedJobs,
    bool SystemAllNormal,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<AuditLogEntry> RecentActivity);

public sealed record DepartmentCoverage(string Name, int Filled, int Total)
{
    public double Ratio => Total > 0 ? (double)Filled / Total : 0;
    public string TrackClass => Ratio >= 0.7 ? "" : Ratio >= 0.5 ? "low" : "crit";
}
