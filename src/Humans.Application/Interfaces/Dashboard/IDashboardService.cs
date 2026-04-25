using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Dashboard;

/// <summary>
/// Orchestrates the member dashboard view: applies business rules to combine
/// membership, application term state, shift discovery, tickets, and participation
/// into a single pre-computed snapshot the web controller can map directly to a
/// view model. Authorization-free; callers are responsible for gating access.
/// </summary>
public interface IDashboardService
{
    Task<MemberDashboardData> GetMemberDashboardAsync(
        Guid userId,
        bool isPrivileged,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-computed dashboard data for a signed-in member.
/// All business rules (term expiry, urgent shift aggregation, signup filtering)
/// are applied by the service; the controller maps this 1:1 onto its view model.
/// </summary>
public record MemberDashboardData(
    Profile? Profile,
    MembershipSnapshot MembershipSnapshot,
    MemberApplication? LatestApplication,
    bool HasPendingApplication,
    MembershipTier CurrentTier,
    LocalDate? TermExpiresAt,
    bool TermExpiresSoon,
    bool TermExpired,
    EventSettings? ActiveEvent,
    IReadOnlyList<DashboardUrgentShift> UrgentShifts,
    IReadOnlyList<DashboardSignup> NextShifts,
    int PendingSignupCount,
    bool HasShiftSignups,
    bool TicketsConfigured,
    bool HasTicket,
    int UserTicketCount,
    ParticipationStatus? ParticipationStatus);

/// <summary>Dashboard-shaped urgent shift entry (domain shift with joined department name).</summary>
public record DashboardUrgentShift(
    Shift Shift,
    string DepartmentName,
    Instant AbsoluteStart,
    int RemainingSlots,
    double UrgencyScore);

/// <summary>Dashboard-shaped confirmed signup entry (domain signup with resolved dept and bounds).</summary>
public record DashboardSignup(
    ShiftSignup Signup,
    string DepartmentName,
    Instant AbsoluteStart,
    Instant AbsoluteEnd);
