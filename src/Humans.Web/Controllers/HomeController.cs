using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

public class HomeController : HumansControllerBase
{
    private readonly IDashboardService _dashboardService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IUserService _userService;
    private readonly IConfiguration _configuration;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        UserManager<User> userManager,
        IDashboardService dashboardService,
        IShiftManagementService shiftMgmt,
        IUserService userService,
        IConfiguration configuration,
        ConfigurationRegistry configRegistry,
        ILogger<HomeController> logger)
        : base(userManager)
    {
        _dashboardService = dashboardService;
        _shiftMgmt = shiftMgmt;
        _userService = userService;
        _configuration = configuration;
        _configRegistry = configRegistry;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
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

        // Profileless accounts go to Guest dashboard
        var hasProfile = User.HasClaim(
            Authorization.RoleAssignmentClaimsTransformation.HasProfileClaimType,
            Authorization.RoleAssignmentClaimsTransformation.ActiveClaimValue);
        if (!hasProfile)
        {
            return RedirectToAction(nameof(Index), "Guest");
        }

        var isPrivileged = User.IsInRole("Admin");
        var data = await _dashboardService.GetMemberDashboardAsync(user.Id, isPrivileged, cancellationToken);

        var viewModel = new DashboardViewModel
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            MembershipStatus = data.MembershipSnapshot.Status,
            HasProfile = data.Profile is not null,
            ProfileComplete = data.Profile is not null && !string.IsNullOrEmpty(data.Profile.FirstName),
            PendingConsents = data.MembershipSnapshot.PendingConsentCount,
            TotalRequiredConsents = data.MembershipSnapshot.RequiredConsentCount,
            IsVolunteerMember = data.MembershipSnapshot.IsVolunteerMember,
            MembershipTier = data.CurrentTier,
            ConsentCheckStatus = data.Profile?.ConsentCheckStatus,
            IsRejected = data.Profile?.RejectedAt is not null,
            RejectionReason = data.Profile?.RejectionReason,
            HasPendingApplication = data.HasPendingApplication,
            LatestApplicationStatus = data.LatestApplication?.Status,
            LatestApplicationDate = data.LatestApplication?.SubmittedAt.ToDateTimeUtc(),
            LatestApplicationTier = data.LatestApplication?.MembershipTier,
            TermExpiresAt = data.TermExpiresAt?.AtMidnight().InUtc().ToDateTimeUtc(),
            TermExpiresSoon = data.TermExpiresSoon,
            TermExpired = data.TermExpired,
            MemberSince = user.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc(),
            EventName = data.ActiveEvent?.EventName,
            IsShiftBrowsingOpen = data.ActiveEvent?.IsShiftBrowsingOpen ?? false,
            HasShiftSignups = data.HasShiftSignups,
            TicketPurchaseUrl = "https://tickets.nobodies.team",
            TicketsConfigured = data.TicketsConfigured,
            HasTicket = data.HasTicket,
            UserTicketCount = data.UserTicketCount,
            EventYear = data.ActiveEvent is not null && data.ActiveEvent.Year > 0 ? data.ActiveEvent.Year : null,
            ParticipationStatus = data.ParticipationStatus,
        };

        ViewData["ShiftCards"] = new ShiftCardsViewModel
        {
            UrgentShifts = data.UrgentShifts
                .Select(u => new UrgentShiftItem
                {
                    Shift = u.Shift,
                    DepartmentName = u.DepartmentName,
                    AbsoluteStart = u.AbsoluteStart,
                    RemainingSlots = u.RemainingSlots,
                    UrgencyScore = u.UrgencyScore,
                })
                .ToList(),
            NextShifts = data.NextShifts
                .Select(s => new MySignupItem
                {
                    Signup = s.Signup,
                    DepartmentName = s.DepartmentName,
                    AbsoluteStart = s.AbsoluteStart,
                    AbsoluteEnd = s.AbsoluteEnd,
                })
                .ToList(),
            PendingCount = data.PendingSignupCount,
        };

        return View("Dashboard", viewModel);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclareNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var activeEvent = await _shiftMgmt.GetActiveAsync();
            if (activeEvent is null || activeEvent.Year <= 0)
            {
                SetError("No active event configured.");
                return RedirectToAction(nameof(Index));
            }

            await _userService.DeclareNotAttendingAsync(user.Id, activeEvent.Year);
            SetSuccess("You've been marked as not attending this year.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to declare not attending for user {UserId}", user.Id);
            SetError("Something went wrong. Please try again.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var activeEvent = await _shiftMgmt.GetActiveAsync();
            if (activeEvent is null || activeEvent.Year <= 0)
            {
                SetError("No active event configured.");
                return RedirectToAction(nameof(Index));
            }

            var undone = await _userService.UndoNotAttendingAsync(user.Id, activeEvent.Year);
            if (undone)
            {
                SetSuccess("Your declaration has been removed.");
            }
            else
            {
                SetError("Could not undo — your status may have been updated by ticket sync.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to undo not attending for user {UserId}", user.Id);
            SetError("Something went wrong. Please try again.");
        }

        return RedirectToAction(nameof(Index));
    }

    public IActionResult Privacy()
    {
        ViewData["DpoEmail"] = _configuration.GetOptionalSetting(
            _configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
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
