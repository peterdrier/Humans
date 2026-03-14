using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = $"{RoleNames.CampAdmin},{RoleNames.Admin}")]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
public class CampAdminController : Controller
{
    private readonly ICampService _campService;
    private readonly UserManager<User> _userManager;

    public CampAdminController(ICampService campService, UserManager<User> userManager)
    {
        _campService = campService;
        _userManager = userManager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var settings = await _campService.GetSettingsAsync();
        var allCamps = await _campService.GetCampsForYearAsync(settings.PublicYear);
        var pendingSeasons = await _campService.GetPendingSeasonsAsync();

        var nameLockDates = settings.OpenSeasons.Count > 0
            ? await _campService.GetNameLockDatesAsync(settings.OpenSeasons)
            : new Dictionary<int, NodaTime.LocalDate?>();

        var vm = new CampAdminViewModel
        {
            PublicYear = settings.PublicYear,
            OpenSeasons = settings.OpenSeasons,
            TotalCamps = allCamps.Count,
            ActiveCamps = allCamps.Count(b => b.Seasons.Any(s =>
                s.Year == settings.PublicYear && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full))),
            NameLockDates = nameLockDates,
            PendingCamps = pendingSeasons.Select(s => new CampCardViewModel
            {
                Id = s.CampId,
                SeasonId = s.Id,
                Slug = s.Camp?.Slug ?? string.Empty,
                Name = s.Name,
                BlurbShort = s.BlurbShort,
                Status = s.Status
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost("Approve/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid seasonId, string? notes)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        try
        {
            await _campService.ApproveSeasonAsync(seasonId, user.Id, notes);
            TempData["SuccessMessage"] = "Season approved.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reject/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid seasonId, string notes)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(notes))
        {
            TempData["ErrorMessage"] = "Rejection notes are required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _campService.RejectSeasonAsync(seasonId, user.Id, notes);
            TempData["SuccessMessage"] = "Season rejected.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("OpenSeason")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSeason([FromForm] int year)
    {
        await _campService.OpenSeasonAsync(year);
        TempData["SuccessMessage"] = $"Season {year} opened for registration.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("CloseSeason/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSeason(int year)
    {
        await _campService.CloseSeasonAsync(year);
        TempData["SuccessMessage"] = $"Season {year} closed for registration.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetPublicYear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPublicYear(int year)
    {
        await _campService.SetPublicYearAsync(year);
        TempData["SuccessMessage"] = $"Public year set to {year}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetNameLockDate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNameLockDate(int year, string lockDate)
    {
        var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(lockDate);
        if (!parseResult.Success)
        {
            TempData["ErrorMessage"] = "Invalid date format.";
            return RedirectToAction(nameof(Index));
        }

        await _campService.SetNameLockDateAsync(year, parseResult.Value);
        TempData["SuccessMessage"] = $"Name lock date for {year} set to {parseResult.Value}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Deactivate/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(Guid seasonId, string? returnSlug)
    {
        try
        {
            await _campService.DeactivateSeasonAsync(seasonId);
            TempData["SuccessMessage"] = "Season deactivated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        if (!string.IsNullOrEmpty(returnSlug))
            return RedirectToAction("Details", "Camp", new { slug = returnSlug });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reactivate/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(Guid seasonId, string? returnSlug)
    {
        try
        {
            await _campService.ReactivateSeasonAsync(seasonId);
            TempData["SuccessMessage"] = "Season reactivated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        if (!string.IsNullOrEmpty(returnSlug))
            return RedirectToAction("Details", "Camp", new { slug = returnSlug });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = RoleNames.Admin)]
    public async Task<IActionResult> Delete([FromForm] Guid campId)
    {
        try
        {
            await _campService.DeleteCampAsync(campId);
            TempData["SuccessMessage"] = "Camp deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }
}
