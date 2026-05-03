using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int ActiveHumans,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<DashboardActivityRow> RecentActivity);

public sealed record DepartmentCoverage(string Name, int Filled, int Total)
{
    public double Ratio => Total > 0 ? (double)Filled / Total : 0;
    public string TrackClass => Ratio >= 0.7 ? "" : Ratio >= 0.5 ? "low" : "crit";
}

public sealed record DashboardActivityRow(AuditAction Action, string Description, Instant OccurredAt);
