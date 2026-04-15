using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Orchestrates the member dashboard snapshot. Pulls from several owning services
/// (profile, membership, applications, shifts, signups, tickets, participation)
/// and applies the business rules (term expiry, urgent-shift aggregation, signup
/// filtering, ticket visibility) that previously lived in <c>HomeController</c>.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly IProfileService _profileService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _shiftSignup;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IUserService _userService;
    private readonly TicketVendorSettings _ticketSettings;
    private readonly IClock _clock;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(
        IProfileService profileService,
        IMembershipCalculator membershipCalculator,
        IApplicationDecisionService applicationDecisionService,
        IShiftManagementService shiftMgmt,
        IShiftSignupService shiftSignup,
        ITicketQueryService ticketQueryService,
        IUserService userService,
        IOptions<TicketVendorSettings> ticketSettings,
        IClock clock,
        ILogger<DashboardService> logger)
    {
        _profileService = profileService;
        _membershipCalculator = membershipCalculator;
        _applicationDecisionService = applicationDecisionService;
        _shiftMgmt = shiftMgmt;
        _shiftSignup = shiftSignup;
        _ticketQueryService = ticketQueryService;
        _userService = userService;
        _ticketSettings = ticketSettings.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MemberDashboardData> GetMemberDashboardAsync(
        Guid userId,
        bool isPrivileged,
        CancellationToken cancellationToken = default)
    {
        _ = isPrivileged; // Retained for future privileged-only fields; no current effect.

        var profile = await _profileService.GetProfileAsync(userId, cancellationToken);
        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, cancellationToken);

        // Applications + term expiry state
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, cancellationToken);
        var latestApplication = applications.Count > 0 ? applications[0] : null;
        var hasPendingApp = latestApplication is not null &&
            latestApplication.Status == ApplicationStatus.Submitted;

        var currentTier = profile?.MembershipTier ?? MembershipTier.Volunteer;
        var (termExpiresAt, termExpiresSoon, termExpired) =
            ComputeTermState(applications, currentTier);

        // Shift cards (urgent shifts + confirmed signups) — guarded, failures never crash the dashboard.
        EventSettings? activeEvent = null;
        try
        {
            activeEvent = await _shiftMgmt.GetActiveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load active event for dashboard");
        }

        var urgentItems = new List<DashboardUrgentShift>();
        var nextShifts = new List<DashboardSignup>();
        var pendingCount = 0;
        var hasShiftSignups = false;

        if (activeEvent is not null && activeEvent.IsShiftBrowsingOpen)
        {
            try
            {
                var urgentShifts = await _shiftMgmt.GetUrgentShiftsAsync(activeEvent.Id, limit: 3);
                foreach (var u in urgentShifts)
                {
                    if (u.Shift is null)
                    {
                        _logger.LogWarning("Skipping urgent shift item because shift data was missing");
                        continue;
                    }

                    try
                    {
                        urgentItems.Add(new DashboardUrgentShift(
                            Shift: u.Shift,
                            DepartmentName: u.DepartmentName ?? "Unknown",
                            AbsoluteStart: u.Shift.GetAbsoluteStart(activeEvent),
                            RemainingSlots: u.RemainingSlots,
                            UrgencyScore: u.UrgencyScore));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to build urgent shift item for shift {ShiftId}", u.Shift.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load urgent shifts for dashboard");
            }

            try
            {
                var now = _clock.GetCurrentInstant();
                var userSignups = await _shiftSignup.GetByUserAsync(userId, activeEvent.Id);
                pendingCount = userSignups
                    .Where(s => s.Status == SignupStatus.Pending)
                    .Select(s => s.SignupBlockId ?? s.Id)
                    .Distinct()
                    .Count();

                foreach (var s in userSignups.Where(s => s.Status == SignupStatus.Confirmed))
                {
                    try
                    {
                        if (s.Shift is null)
                        {
                            _logger.LogWarning("Skipping signup {SignupId} on dashboard because shift data was missing", s.Id);
                            continue;
                        }

                        var item = new DashboardSignup(
                            Signup: s,
                            DepartmentName: s.Shift.Rota?.Team?.Name ?? "Unknown",
                            AbsoluteStart: s.Shift.GetAbsoluteStart(activeEvent),
                            AbsoluteEnd: s.Shift.GetAbsoluteEnd(activeEvent));
                        if (item.AbsoluteEnd > now)
                            nextShifts.Add(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to build shift item for signup {SignupId}", s.Id);
                    }
                }

                nextShifts = nextShifts.OrderBy(i => i.AbsoluteStart).Take(3).ToList();
                hasShiftSignups = nextShifts.Count > 0 || pendingCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user signups for dashboard");
            }
        }

        // Ticket state
        var ticketsConfigured = _ticketSettings.IsConfigured;
        var hasTicket = false;
        var userTicketCount = 0;
        try
        {
            if (ticketsConfigured)
            {
                userTicketCount = await _ticketQueryService.GetUserTicketCountAsync(userId);
                hasTicket = userTicketCount > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ticket status for user {UserId}", userId);
        }

        // Event participation
        ParticipationStatus? participationStatus = null;
        try
        {
            if (activeEvent is not null && activeEvent.Year > 0)
            {
                var participation = await _userService.GetParticipationAsync(userId, activeEvent.Year, cancellationToken);
                participationStatus = participation?.Status;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load participation status for user {UserId}", userId);
        }

        return new MemberDashboardData(
            Profile: profile,
            MembershipSnapshot: membershipSnapshot,
            LatestApplication: latestApplication,
            HasPendingApplication: hasPendingApp,
            CurrentTier: currentTier,
            TermExpiresAt: termExpiresAt,
            TermExpiresSoon: termExpiresSoon,
            TermExpired: termExpired,
            ActiveEvent: activeEvent,
            UrgentShifts: urgentItems,
            NextShifts: nextShifts,
            PendingSignupCount: pendingCount,
            HasShiftSignups: hasShiftSignups,
            TicketsConfigured: ticketsConfigured,
            HasTicket: hasTicket,
            UserTicketCount: userTicketCount,
            ParticipationStatus: participationStatus);
    }

    private (LocalDate? ExpiresAt, bool ExpiresSoon, bool Expired) ComputeTermState(
        IReadOnlyList<MemberApplication> applications,
        MembershipTier currentTier)
    {
        if (currentTier == MembershipTier.Volunteer)
        {
            return (null, false, false);
        }

        var latestApprovedApp = applications
            .Where(a => a.Status == ApplicationStatus.Approved
                && a.MembershipTier == currentTier
                && a.TermExpiresAt is not null)
            .OrderByDescending(a => a.TermExpiresAt)
            .FirstOrDefault();

        if (latestApprovedApp?.TermExpiresAt is null)
        {
            return (null, false, false);
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var expiryDate = latestApprovedApp.TermExpiresAt.Value;
        var expired = expiryDate < today;
        var expiresSoon = !expired && expiryDate <= today.PlusDays(90);

        return (expiryDate, expiresSoon, expired);
    }
}
