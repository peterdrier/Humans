using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

public class HomeController : HumansControllerBase
{
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IProfileService _profileService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _shiftSignup;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly TicketVendorSettings _ticketSettings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        UserManager<User> userManager,
        IMembershipCalculator membershipCalculator,
        IProfileService profileService,
        IApplicationDecisionService applicationDecisionService,
        IShiftManagementService shiftMgmt,
        IShiftSignupService shiftSignup,
        IConfiguration configuration,
        IClock clock,
        IOptions<TicketVendorSettings> ticketSettings,
        ITicketQueryService ticketQueryService,
        ILogger<HomeController> logger)
        : base(userManager)
    {
        _membershipCalculator = membershipCalculator;
        _profileService = profileService;
        _applicationDecisionService = applicationDecisionService;
        _shiftMgmt = shiftMgmt;
        _shiftSignup = shiftSignup;
        _configuration = configuration;
        _clock = clock;
        _ticketSettings = ticketSettings.Value;
        _ticketQueryService = ticketQueryService;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return View();
        }

        // Show dashboard for logged in users
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return View();
        }

        var profile = await _profileService.GetProfileAsync(user.Id);

        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(user.Id);

        // Get all applications for the user
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

        var hasPendingApp = latestApplication is not null &&
            latestApplication.Status == ApplicationStatus.Submitted;

        // Get term expiry from latest approved application for the user's current tier
        var currentTier = profile?.MembershipTier ?? MembershipTier.Volunteer;
        DateTime? termExpiresAt = null;
        var termExpiresSoon = false;
        var termExpired = false;

        if (currentTier != MembershipTier.Volunteer)
        {
            var latestApprovedApp = applications
                .Where(a => a.Status == ApplicationStatus.Approved
                    && a.MembershipTier == currentTier
                    && a.TermExpiresAt is not null)
                .OrderByDescending(a => a.TermExpiresAt)
                .FirstOrDefault();

            if (latestApprovedApp?.TermExpiresAt is not null)
            {
                var today = _clock.GetCurrentInstant().InUtc().Date;
                var expiryDate = latestApprovedApp.TermExpiresAt.Value;
                termExpiresAt = expiryDate.AtMidnight().InUtc().ToDateTimeUtc();
                termExpired = expiryDate < today;
                termExpiresSoon = !termExpired && expiryDate <= today.PlusDays(90);
            }
        }

        var viewModel = new DashboardViewModel
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            MembershipStatus = membershipSnapshot.Status,
            HasProfile = profile is not null,
            ProfileComplete = profile is not null && !string.IsNullOrEmpty(profile.FirstName),
            PendingConsents = membershipSnapshot.PendingConsentCount,
            TotalRequiredConsents = membershipSnapshot.RequiredConsentCount,
            IsVolunteerMember = membershipSnapshot.IsVolunteerMember,
            MembershipTier = currentTier,
            ConsentCheckStatus = profile?.ConsentCheckStatus,
            IsRejected = profile?.RejectedAt is not null,
            RejectionReason = profile?.RejectionReason,
            HasPendingApplication = hasPendingApp,
            LatestApplicationStatus = latestApplication?.Status,
            LatestApplicationDate = latestApplication?.SubmittedAt.ToDateTimeUtc(),
            LatestApplicationTier = latestApplication?.MembershipTier,
            TermExpiresAt = termExpiresAt,
            TermExpiresSoon = termExpiresSoon,
            TermExpired = termExpired,
            MemberSince = user.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc()
        };

        // Shift cards — fully guarded, failures must never crash the homepage
        try
        {
            var activeEvent = await _shiftMgmt.GetActiveAsync();
            if (activeEvent is not null)
            {
                viewModel.EventName = activeEvent.EventName;
                viewModel.IsShiftBrowsingOpen = activeEvent.IsShiftBrowsingOpen;
            }
            if (activeEvent is not null && activeEvent.IsShiftBrowsingOpen)
            {
                var urgentShifts = await _shiftMgmt.GetUrgentShiftsAsync(activeEvent.Id, limit: 3);

                var urgentItems = new List<UrgentShiftItem>();
                foreach (var u in urgentShifts)
                {
                    if (u.Shift is null)
                    {
                        _logger.LogWarning("Skipping urgent shift item because shift data was missing");
                        continue;
                    }

                    try
                    {
                        urgentItems.Add(new UrgentShiftItem
                        {
                            Shift = u.Shift,
                            DepartmentName = u.DepartmentName ?? "Unknown",
                            AbsoluteStart = u.Shift.GetAbsoluteStart(activeEvent),
                            RemainingSlots = u.RemainingSlots,
                            UrgencyScore = u.UrgencyScore
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to build urgent shift item for shift {ShiftId}", u.Shift.Id);
                    }
                }

                var nextShifts = new List<MySignupItem>();
                var pendingCount = 0;
                try
                {
                    var now = _clock.GetCurrentInstant();
                    var userSignups = await _shiftSignup.GetByUserAsync(user.Id, activeEvent.Id);
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

                            var item = new MySignupItem
                            {
                                Signup = s,
                                DepartmentName = s.Shift.Rota?.Team?.Name ?? "Unknown",
                                AbsoluteStart = s.Shift.GetAbsoluteStart(activeEvent),
                                AbsoluteEnd = s.Shift.GetAbsoluteEnd(activeEvent)
                            };
                            if (item.AbsoluteEnd > now)
                                nextShifts.Add(item);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to build shift item for signup {SignupId}", s.Id);
                        }
                    }

                    nextShifts = nextShifts.OrderBy(i => i.AbsoluteStart).Take(3).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load user signups for dashboard");
                }

                ViewData["ShiftCards"] = new ShiftCardsViewModel
                {
                    UrgentShifts = urgentItems,
                    NextShifts = nextShifts,
                    PendingCount = pendingCount
                };

                // Pass shift signup state to ThingsToDo wizard
                viewModel.HasShiftSignups = nextShifts.Count > 0 || pendingCount > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load shift cards for dashboard");
        }

        // Per-user ticket status — always renders, shows warning when unconfigured
        viewModel.TicketPurchaseUrl = "https://tickets.nobodies.team";
        viewModel.TicketsConfigured = _ticketSettings.IsConfigured;
        try
        {
            if (_ticketSettings.IsConfigured)
            {
                var ticketCount = await _ticketQueryService.GetUserTicketCountAsync(user.Id);
                viewModel.HasTicket = ticketCount > 0;
                viewModel.UserTicketCount = ticketCount;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ticket status for user {UserId}", user.Id);
        }

        return View("Dashboard", viewModel);
    }

    public IActionResult Privacy()
    {
        ViewData["DpoEmail"] = _configuration["Email:DpoAddress"];
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("/Home/Error/{statusCode?}")]
    public IActionResult Error(int? statusCode = null)
    {
        if (statusCode == 404)
        {
            return View("Error404");
        }

        return View();
    }
}
