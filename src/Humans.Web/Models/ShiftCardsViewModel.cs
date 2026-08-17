
using Humans.Web.Services.Dashboard;

namespace Humans.Web.Models;

/// <summary>
/// Model for <c>Views/Home/_ShiftCards.cshtml</c>, the dashboard's shift block.
/// </summary>
/// <remarks>
/// Shell's, not the Shifts section's: <c>HomeController</c> builds it entirely from
/// <c>IDashboardService</c>'s own <see cref="DashboardUrgentShift"/> /
/// <see cref="DashboardSignup"/> records, so it names nothing inside Humans.Shifts and
/// stayed behind at that section's G5 (nobodies-collective/Humans#866).
/// </remarks>
public sealed class ShiftCardsViewModel
{
    public IReadOnlyList<DashboardSignup> NextShifts { get; set; } = [];
    public int PendingCount { get; set; }
    public IReadOnlyList<DashboardUrgentShift> UrgentShifts { get; set; } = [];
}
