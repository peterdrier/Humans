using Humans.Application.Configuration;
using Humans.Application.Helpers;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Dashboard;

/// <summary>
/// Orchestrates the member dashboard snapshot. Pulls from several owning services
/// (profile, membership, applications, shifts, signups, tickets, participation)
/// and applies the business rules (term expiry, urgent-shift aggregation, signup
/// filtering, ticket visibility) that previously lived in <c>HomeController</c>.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftView _shiftView;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly TicketVendorSettings _ticketSettings;
    private readonly IClock _clock;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(
        IMembershipCalculator membershipCalculator,
        IApplicationDecisionService applicationDecisionService,
        IShiftManagementService shiftMgmt,
        IShiftView shiftView,
        ITicketQueryService ticketQueryService,
        IUserService userService,
        ITeamService teamService,
        IOptions<TicketVendorSettings> ticketSettings,
        IClock clock,
        ILogger<DashboardService> logger)
    {
        _membershipCalculator = membershipCalculator;
        _applicationDecisionService = applicationDecisionService;
        _shiftMgmt = shiftMgmt;
        _shiftView = shiftView;
        _ticketQueryService = ticketQueryService;
        _userService = userService;
        _teamService = teamService;
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

        var userInfo = await _userService.GetUserInfoAsync(userId, cancellationToken);
        var profile = userInfo?.Profile;
        // T-09 (issue #720): read tag-preference count from the cached
        // ShiftUserView rather than the repo-backed
        // IShiftManagementService.GetVolunteerTagPreferencesAsync — same data,
        // cache hit replaces a DB round trip on every dashboard render.
        var userView = await _shiftView.GetUserAsync(userId, cancellationToken);
        var hasShiftTagPreferences = userView.TagPreferences.Count > 0;
        var dashboardProfile = profile is null
            ? null
            : new DashboardProfile(
                ProfileComplete: !string.IsNullOrEmpty(profile.FirstName),
                CompletionPercent: ProfileCompletion.ComputePercent(profile, hasShiftTagPreferences),
                ConsentCheckStatus: profile.ConsentCheckStatus,
                IsRejected: profile.RejectedAt is not null,
                RejectionReason: profile.RejectionReason);
        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, cancellationToken);

        // Applications + term expiry state
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, cancellationToken);
        var latestApplication = applications.Count > 0 ? applications[0] : null;
        var latestApplicationSnapshot = latestApplication is null
            ? null
            : new DashboardApplication(
                latestApplication.Status,
                latestApplication.SubmittedAt,
                latestApplication.MembershipTier);
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
                    try
                    {
                        urgentItems.Add(new DashboardUrgentShift(
                            RotaName: u.Shift.Rota?.Name ?? "Unknown",
                            DepartmentName: u.DepartmentName,
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
                // Pilot consumer for IShiftView (#720): the user-signup read
                // that previously hit the DB on every dashboard render now
                // comes from the cached ShiftUserView. The view holds every
                // signup row for the user; filter to the active event in
                // memory. Shift.Rota.EventSettings is loaded by the inner
                // ShiftViewService. Cache hits complete synchronously via
                // ValueTask (no Task allocation, no thread hop).
                var userSignups = userView.Signups
                    .Where(s => s.Shift?.Rota?.EventSettingsId == activeEvent.Id)
                    .ToList();
                pendingCount = userSignups
                    .Where(s => s.Status == SignupStatus.Pending)
                    .Select(s => s.SignupBlockId ?? s.Id)
                    .Distinct()
                    .Count();

                var confirmedSignups = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
                var dashboardTeamIds = confirmedSignups
                    .Where(s => s.Shift?.Rota is not null)
                    .Select(s => s.Shift.Rota.TeamId)
                    .Distinct()
                    .ToList();
                var teamsById = await _teamService.GetTeamsAsync();
                var teamNames = dashboardTeamIds
                    .Where(teamsById.ContainsKey)
                    .ToDictionary(id => id, id => teamsById[id].Name);

                foreach (var s in confirmedSignups)
                {
                    try
                    {
                        // Shift/Rota navs are populated by ShiftSignupRepository.GetByUserAsync
                        // (Include chain) and the upstream .Where filtered to non-null Rota.
                        var item = new DashboardSignup(
                            RotaName: s.Shift.Rota.Name,
                            DepartmentName: teamNames.GetValueOrDefault(s.Shift.Rota.TeamId, "Unknown"),
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
                var info = await _userService.GetUserInfoAsync(userId, cancellationToken);
                participationStatus = info?.EventParticipations
                    .FirstOrDefault(p => p.Year == activeEvent.Year)?.Status;
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("Dashboard participation load cancelled for user {UserId}: {Reason}", userId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load participation status for user {UserId}", userId);
        }

        return new MemberDashboardData(
            Profile: dashboardProfile,
            MembershipSnapshot: membershipSnapshot,
            LatestApplication: latestApplicationSnapshot,
            HasPendingApplication: hasPendingApp,
            CurrentTier: currentTier,
            TermExpiresAt: termExpiresAt,
            TermExpiresSoon: termExpiresSoon,
            TermExpired: termExpired,
            ActiveEvent: activeEvent is null
                ? null
                : new DashboardEvent(activeEvent.EventName, activeEvent.IsShiftBrowsingOpen, activeEvent.Year),
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
        IReadOnlyList<UserApplicationSnapshot> applications,
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
