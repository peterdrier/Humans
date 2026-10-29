using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Base.Controllers;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Shifts.Controllers;

// The wider policy for entry; views gate the privileged sub-panels on ShiftsPolicies.Read.
[Authorize(Policy = ShiftsPolicies.DepartmentRead)]
[Route("Shifts/Dashboard")]
internal sealed class ShiftDashboardController(
    IBurnSettingsService burnSettings,
    IShiftManagement shiftMgmt,
    IShiftSignupService signupService,
    IUserService userService,
    ShiftDashboardPageBuilder pageBuilder,
    ShiftVolunteerSearchBuilder volunteerSearchBuilder,
    ILogger<ShiftDashboardController> logger) : HumansControllerBase(userService)
{
    private static LocalDate? ParseIsoDateOrNull(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parsed = LocalDatePattern.Iso.Parse(raw);
        return parsed.Success ? parsed.Value : null;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        Guid? departmentId,
        Guid? rotaId,
        string? startDate,
        string? endDate,
        TrendWindow? trendWindow,
        ShiftPeriod? period,
        BuildSubPeriod? subPeriod,
        CancellationToken ct = default)
    {
        var es = await burnSettings.GetActiveAsync(ct);
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction("Index", "Home");
        }

        var filterStartDate = ParseIsoDateOrNull(startDate);
        var filterEndDate = ParseIsoDateOrNull(endDate);
        var (activeStart, activeEnd) = ShiftFilterResolver.Resolve(period, filterStartDate, filterEndDate);

        var model = await pageBuilder.BuildAsync(new ShiftDashboardPageRequest(
            es,
            departmentId,
            rotaId,
            startDate,
            endDate,
            filterStartDate,
            filterEndDate,
            activeStart,
            activeEnd,
            trendWindow ?? TrendWindow.Last30Days,
            period,
            subPeriod), ct);

        return View(model);
    }

    [Authorize(Policy = ShiftsPolicies.Read)]
    [HttpGet("PostEventStats")]
    public async Task<IActionResult> PostEventStats(CancellationToken ct = default)
    {
        var es = await burnSettings.GetActiveAsync(ct);
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction("Index", "Home");
        }

        var stats = await shiftMgmt.GetPostEventStatsAsync(es.Id, ct);
        if (stats is null)
        {
            SetError("Could not load post-event statistics.");
            return RedirectToAction(nameof(Index));
        }

        ViewData["Title"] = "Post-Event Stats";
        var ordered = stats with
        {
            Departments = stats.Departments
                .OrderBy(r => r.DepartmentName, StringComparer.Ordinal)
                .ToList()
        };
        return View(ordered);
    }

    // Narrower than the controller's policy: subteam managers can't reach it.
    [Authorize(Policy = ShiftsPolicies.Manage)]
    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query, CancellationToken ct = default)
    {
        try
        {
            var result = await volunteerSearchBuilder.BuildForShiftAsync(
                await shiftMgmt.GetShiftByIdAsync(shiftId, ct),
                query, ct);
            return result.Status switch
            {
                VolunteerSearchBuildStatus.EmptyQuery => Json(Array.Empty<VolunteerSearchResult>()),
                VolunteerSearchBuildStatus.NotFound => NotFound(),
                VolunteerSearchBuildStatus.Success => Json(result.Results),
                _ => throw new InvalidOperationException($"Unexpected volunteer search status '{result.Status}'.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    // Narrower than the controller's policy: subteam managers and Board can't assign.
    [Authorize(Policy = ShiftsPolicies.Manage)]
    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(Guid shiftId, Guid userId)
    {
        var (currentUserNotFound, currentUser) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var result = await signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
        if (result.Success)
        {
            SetSuccess("Volunteer assigned to shift.");
        }
        else
        {
            SetError(result.Error ?? "Failed to assign volunteer.");
        }

        return RedirectToAction(nameof(Index));
    }
}
