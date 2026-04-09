using System.Text;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
public class CampAdminController : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly ILogger<CampAdminController> _logger;

    public CampAdminController(
        ICampService campService,
        UserManager<User> userManager,
        ILogger<CampAdminController> logger)
        : base(userManager)
    {
        _campService = campService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var settings = await _campService.GetSettingsAsync();
            var allCamps = await _campService.GetAllCampsForYearAsync(settings.PublicYear);
            var pendingSeasons = await _campService.GetPendingSeasonsAsync();

            var nameLockDates = settings.OpenSeasons.Count > 0
                ? await _campService.GetNameLockDatesAsync(settings.OpenSeasons)
                : new Dictionary<int, NodaTime.LocalDate?>();

            var withdrawnSeasons = allCamps
                .SelectMany(c => c.Seasons
                    .Where(s => s.Year == settings.PublicYear && s.Status == CampSeasonStatus.Withdrawn)
                    .Select(s => new CampCardViewModel
                    {
                        Id = c.Id,
                        SeasonId = s.Id,
                        Slug = c.Slug,
                        Name = s.Name,
                        BlurbShort = s.BlurbShort,
                        Status = s.Status
                    }))
                .ToList();

            // Load camps with leads for the summary table
            var activeStatuses = new[] { CampSeasonStatus.Active, CampSeasonStatus.Full };
            var campsWithLeads = await _campService.GetCampsWithLeadsForYearAsync(settings.PublicYear, activeStatuses);

            var summaries = campsWithLeads.Select(c =>
            {
                var season = c.Seasons.FirstOrDefault();
                return new CampSummaryRowViewModel
                {
                    Name = season?.Name ?? c.Slug,
                    Slug = c.Slug,
                    AcceptingMembers = season?.AcceptingMembers.ToString() ?? "—",
                    MemberCount = season?.MemberCount ?? 0,
                    Zone = season?.SoundZone?.ToString() ?? "—",
                    SpaceRequirement = season?.SpaceRequirement?.ToString() ?? "—",
                    YearsParticipating = c.TimesAtNowhere,
                    Leads = c.Leads
                        .Where(l => l.IsActive)
                        .Select(l => new CampLeadViewModel
                        {
                            LeadId = l.Id,
                            UserId = l.UserId,
                            DisplayName = l.User.DisplayName
                        }).ToList()
                };
            }).ToList();

            var vm = new CampAdminViewModel
            {
                PublicYear = settings.PublicYear,
                OpenSeasons = settings.OpenSeasons,
                TotalCamps = allCamps.Count,
                ActiveCamps = allCamps.Count(b => b.Seasons.Any(s =>
                    s.Year == settings.PublicYear && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full))),
                WithdrawnCamps = withdrawnSeasons,
                NameLockDates = nameLockDates,
                AllCampSummaries = summaries,
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Barrios admin page");
            SetError("Failed to load Barrios admin page.");
            return RedirectToAction("Index", "Admin");
        }
    }

    [HttpPost("Approve/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid seasonId, string? notes)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();

        try
        {
            await _campService.ApproveSeasonAsync(seasonId, user.Id, notes);
            SetSuccess("Season approved.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to approve camp season {SeasonId} for admin {UserId}", seasonId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reject/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid seasonId, string notes)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(notes))
        {
            SetError("Rejection notes are required.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _campService.RejectSeasonAsync(seasonId, user.Id, notes);
            SetSuccess("Season rejected.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to reject camp season {SeasonId} for admin {UserId}", seasonId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("OpenSeason")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSeason([FromForm] int year)
    {
        try
        {
            await _campService.OpenSeasonAsync(year);
            SetSuccess($"Season {year} opened for registration.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open season {Year}", year);
            SetError($"Failed to open season: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("CloseSeason/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSeason(int year)
    {
        try
        {
            await _campService.CloseSeasonAsync(year);
            SetSuccess($"Season {year} closed for registration.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close season {Year}", year);
            SetError($"Failed to close season: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetPublicYear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPublicYear(int year)
    {
        try
        {
            await _campService.SetPublicYearAsync(year);
            SetSuccess($"Public year set to {year}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set public year to {Year}", year);
            SetError($"Failed to set public year: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetNameLockDate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNameLockDate(int year, string lockDate)
    {
        var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(lockDate);
        if (!parseResult.Success)
        {
            SetError("Invalid date format.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _campService.SetNameLockDateAsync(year, parseResult.Value);
            SetSuccess($"Name lock date for {year} set to {parseResult.Value}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set name lock date for {Year}", year);
            SetError($"Failed to set name lock date: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reactivate/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(Guid seasonId, string? returnSlug)
    {
        try
        {
            await _campService.ReactivateSeasonAsync(seasonId);
            SetSuccess("Season status updated.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to reactivate camp season {SeasonId}", seasonId);
            SetError(ex.Message);
        }

        if (!string.IsNullOrEmpty(returnSlug))
            return RedirectToAction(nameof(CampController.Details), "Camp", new { slug = returnSlug });
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export")]
    public async Task<IActionResult> ExportCamps()
    {
        try
        {
            var settings = await _campService.GetSettingsAsync();
            var year = settings.PublicYear;

            var camps = await _campService.GetCampsWithLeadsForYearAsync(year);

            var csv = new StringBuilder();
            csv.AppendCsvRow(
                "Name", "Slug", "Status", "Contact Email", "Contact Phone",
                "Leads", "Languages", "Member Count",
                "Space Requirement", "Sound Zone", "Containers", "Electrical Grid",
                "Accepting Members", "Kids Welcome", "Adult Playspace",
                "Vibes", "Swiss Camp", "Times Participating");

            foreach (var camp in camps)
            {
                var season = camp.Seasons.FirstOrDefault();
                if (season is null) continue;

                var leads = string.Join("; ", camp.Leads
                    .Where(l => l.IsActive)
                    .Select(l => $"{l.User.DisplayName} <{l.User.Email}>"));

                var vibes = season.Vibes.Count > 0
                    ? string.Join(", ", season.Vibes)
                    : "";

                csv.AppendCsvRow(
                    season.Name,
                    camp.Slug,
                    season.Status,
                    camp.ContactEmail,
                    camp.ContactPhone,
                    leads,
                    season.Languages,
                    season.MemberCount,
                    season.SpaceRequirement?.ToString() ?? "",
                    season.SoundZone?.ToString() ?? "",
                    season.ContainerCount,
                    season.ElectricalGrid?.ToString() ?? "",
                    season.AcceptingMembers,
                    season.KidsWelcome,
                    season.AdultPlayspace,
                    vibes,
                    camp.IsSwissCamp ? "Yes" : "No",
                    camp.TimesAtNowhere);
            }

            return File(Encoding.UTF8.GetBytes(csv.ToString()),
                "text/csv", $"barrios-{year}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export barrios");
            SetError("Failed to export barrios.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Delete([FromForm] Guid campId)
    {
        try
        {
            await _campService.DeleteCampAsync(campId);
            SetSuccess("Camp deleted.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to delete camp {CampId}", campId);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }
}
