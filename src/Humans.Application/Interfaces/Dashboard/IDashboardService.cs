using Humans.Application.DTOs;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Dashboard;

/// <summary>
/// Orchestrates the member dashboard view: applies business rules to combine
/// membership, application term state, shift discovery, tickets, and participation
/// into a single pre-computed snapshot the web controller can map directly to a
/// view model. Authorization-free; callers are responsible for gating access.
/// </summary>
public interface IDashboardService : IApplicationService
{
    Task<MemberDashboardData> GetMemberDashboardAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-computed dashboard data for a signed-in member.
/// All business rules (term expiry, urgent shift aggregation, signup filtering)
/// are applied by the service; the controller maps this 1:1 onto its view model.
/// </summary>
public record MemberDashboardData(
    DashboardProfile? Profile,
    MembershipSnapshot MembershipSnapshot,
    DashboardApplication? LatestApplication,
    bool HasPendingApplication,
    MembershipTier CurrentTier,
    LocalDate? TermExpiresAt,
    bool TermExpiresSoon,
    bool TermExpired,
    DashboardEvent? ActiveEvent,
    IReadOnlyList<DashboardUrgentShift> UrgentShifts,
    IReadOnlyList<DashboardSignup> NextShifts,
    int PendingSignupCount,
    bool HasShiftSignups,
    bool TicketsConfigured,
    bool HasTicket,
    int UserTicketCount,
    ParticipationStatus? ParticipationStatus);

public record DashboardProfile(
    bool ProfileComplete,
    int CompletionPercent,
    ConsentCheckStatus? ConsentCheckStatus,
    bool IsRejected,
    string? RejectionReason);

public record DashboardApplication(
    ApplicationStatus Status,
    Instant SubmittedAt,
    MembershipTier MembershipTier);

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
