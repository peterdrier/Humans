using Humans.Governance.Contracts;
using NodaTime;

using Humans.Base.Interfaces;

namespace Humans.Web.Services.Dashboard;

/// <summary>
/// Orchestrates the member dashboard view: applies business rules to combine
/// membership, profile state and shift discovery into a single pre-computed
/// snapshot the web controller can map directly to a view model. Term, ticket and
/// participation state left with the sections that contribute those cards.
/// Authorization-free; callers are responsible for gating access.
/// </summary>
public interface IDashboardService : IApplicationService
{
    Task<MemberDashboardData> GetMemberDashboardAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-computed dashboard data for a signed-in member.
/// All business rules (urgent shift aggregation, signup filtering) are applied by
/// the service; the controller maps this 1:1 onto its view model.
/// </summary>
public record MemberDashboardData(
    DashboardProfile? Profile,
    MembershipSnapshot MembershipSnapshot,
    DashboardEvent? ActiveEvent,
    IReadOnlyList<DashboardUrgentShift> UrgentShifts,
    IReadOnlyList<DashboardSignup> NextShifts,
    int PendingSignupCount);

public record DashboardProfile(
    bool ProfileComplete,
    bool IsRejected,
    string? RejectionReason);

public record DashboardEvent(
    string EventName,
    bool IsShiftBrowsingOpen,
    int Year);

/// <summary>Dashboard-shaped urgent shift entry with joined department and rota display data.</summary>
public record DashboardUrgentShift(
    string RotaName,
    string DepartmentName,
    Instant AbsoluteStart,
    int RemainingSlots,
    double UrgencyScore);

/// <summary>Dashboard-shaped confirmed signup entry with resolved dept, rota, and bounds.</summary>
public record DashboardSignup(
    string RotaName,
    string DepartmentName,
    Instant AbsoluteStart,
    Instant AbsoluteEnd);
