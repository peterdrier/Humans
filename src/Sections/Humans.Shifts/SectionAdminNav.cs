using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Shifts;

/// <summary>Shifts' admin sidebar contribution — the "Shifts" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Shifts", [
            new("Dashboard",         "ShiftDashboard",      "Index",          null, null, "fa-solid fa-gauge",            PolicyNames.ShiftDepartmentManager),
            new("Summary by camp",   "Shifts",              "Summary",        null, null, "fa-solid fa-campground",       PolicyNames.ShiftDepartmentManager),
            new("Volunteer tracking","VolunteerTracking",   "Index",          null, null, "fa-solid fa-user-clock",       PolicyNames.ShiftDashboardAccess),
            new("Workload",          "ShiftWorkloadAdmin",  "Index",          null, null, "fa-solid fa-scale-unbalanced", PolicyNames.ShiftDashboardAccess),
            new("Post-event stats",  "ShiftDashboard",      "PostEventStats", null, null, "fa-solid fa-chart-bar",        PolicyNames.ShiftDashboardAccess),
            new("Orphan signups",    "Shifts",              "OrphanSignups",  null, null, "fa-solid fa-user-secret",      PolicyNames.AdminOnly)
        ], Weight: 20)
    ];
}
