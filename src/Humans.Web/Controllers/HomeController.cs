using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Configuration;
using Humans.Web.Models;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

using Humans.Web.Services.Dashboard;

namespace Humans.Web.Controllers;

public class HomeController(
    IUserService userService,
    IDashboardService dashboardService,
    IBurnSettingsService burnSettings,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<HomeController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            return View();
        }

        var user = await GetCurrentUserInfoAsync();
        if (user is null)
        {
            return View();
        }

        // Name-only access: the MembershipRequiredFilter routes non-Active users away, so any
        // authenticated user reaching the dashboard is Active (named, not suspended/etc.).
        var data = await dashboardService.GetMemberDashboardAsync(user.Id, cancellationToken);

        var viewModel = new DashboardViewModel
        {
            UserId = user.Id,
            DisplayName = user.BurnerName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasProfile = data.Profile is not null,
            ProfileComplete = data.Profile?.ProfileComplete ?? false,
            PendingConsents = data.MembershipSnapshot.PendingConsentCount,
            TotalRequiredConsents = data.MembershipSnapshot.RequiredConsentCount,
            IsVolunteerMember = data.MembershipSnapshot.IsVolunteerMember,
            IsRejected = data.Profile?.IsRejected ?? false,
            RejectionReason = data.Profile?.RejectionReason,
            MemberSince = user.CreatedAt.ToDateTimeUtc(),
            LastLogin = user.LastLoginAt?.ToDateTimeUtc(),
            EventName = data.ActiveEvent?.EventName,
            IsShiftBrowsingOpen = data.ActiveEvent?.IsShiftBrowsingOpen ?? false,
        };

        ViewData["ShiftCards"] = new ShiftCardsViewModel
        {
            UrgentShifts = data.UrgentShifts,
            NextShifts = data.NextShifts,
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
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return RedirectToAction(nameof(Index));
            }

            await _userService.DeclareNotAttendingAsync(user.Id, eventYear.Value);
            SetSuccess("You've been marked as not attending this year.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to declare not attending for user {UserId}", user.Id);
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
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return RedirectToAction(nameof(Index));
            }

            var undone = await _userService.UndoNotAttendingAsync(user.Id, eventYear.Value);
            SetUndoNotAttendingResult(undone);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to undo not attending for user {UserId}", user.Id);
            SetError("Something went wrong. Please try again.");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<int?> GetActiveEventYearOrSetErrorAsync()
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            return activeEvent.Year;
        }

        SetError("No active event configured.");
        return null;
    }

    private void SetUndoNotAttendingResult(bool undone)
    {
        if (undone)
        {
            SetSuccess("Your declaration has been removed.");
            return;
        }

        SetError("Could not undo — your status may have been updated by ticket sync.");
    }
    public IActionResult Privacy()
    {
        ViewData["DpoEmail"] = configuration.GetOptionalSetting(
            configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
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

