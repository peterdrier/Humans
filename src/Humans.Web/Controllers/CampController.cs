using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Web.Authorization;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Web.Controllers;

[Route("Barrios")]
[Route("Camps")]
public class CampController : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly IClock _clock;
    private readonly ILogger<CampController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CampController(
        ICampService campService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<CampController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _campService = campService;
        _clock = clock;
        _logger = logger;
        _localizer = localizer;
    }

    // ======================================================================
    // Public routes
    // ======================================================================

    [AllowAnonymous]
    [HttpGet("")]
    public async Task<IActionResult> Index(CampFilterViewModel? filters)
    {
        var settings = await _campService.GetSettingsAsync();
        var year = settings.PublicYear;
        var camps = await _campService.GetCampsForYearAsync(year);

        var cards = camps.Select(b =>
        {
            var season = b.Seasons.FirstOrDefault(s => s.Year == year);
            var firstImage = b.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            return new CampCardViewModel
            {
                Id = b.Id,
                Slug = b.Slug,
                Name = season?.Name ?? b.Slug,
                BlurbShort = season?.BlurbShort ?? string.Empty,
                ImageUrl = firstImage != null ? $"/{firstImage.StoragePath}" : null,
                Vibes = season?.Vibes ?? new List<CampVibe>(),
                AcceptingMembers = season?.AcceptingMembers ?? YesNoMaybe.No,
                KidsWelcome = season?.KidsWelcome ?? YesNoMaybe.No,
                SoundZone = season?.SoundZone,
                Status = season?.Status ?? CampSeasonStatus.Pending,
                TimesAtNowhere = b.TimesAtNowhere
            };
        }).ToList();

        // Apply filters
        if (filters?.Vibe.HasValue == true)
            cards = cards.Where(c => c.Vibes.Contains(filters.Vibe.Value)).ToList();
        if (filters?.SoundZone.HasValue == true)
            cards = cards.Where(c => c.SoundZone == filters.SoundZone.Value).ToList();
        if (filters?.KidsFriendly == true)
            cards = cards.Where(c => c.KidsWelcome == YesNoMaybe.Yes).ToList();
        if (filters?.AcceptingMembers == true)
            cards = cards.Where(c => c.AcceptingMembers == YesNoMaybe.Yes).ToList();

        var myCamps = new List<CampCardViewModel>();
        var user = await GetCurrentUserAsync();
        if (user is not null)
        {
            var allUserCamps = await _campService.GetCampsByLeadUserIdAsync(user.Id);
            myCamps = allUserCamps
                .Where(b => b.Seasons.Any(s => s.Year == year &&
                    s.Status != CampSeasonStatus.Active && s.Status != CampSeasonStatus.Full))
                .Where(b => !cards.Any(c => c.Id == b.Id)) // exclude already-shown
                .Select(b =>
                {
                    var season = b.Seasons.FirstOrDefault(s => s.Year == year);
                    var firstImage = b.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
                    return new CampCardViewModel
                    {
                        Id = b.Id,
                        Slug = b.Slug,
                        Name = season?.Name ?? b.Slug,
                        BlurbShort = season?.BlurbShort ?? string.Empty,
                        ImageUrl = firstImage != null ? $"/{firstImage.StoragePath}" : null,
                        Vibes = season?.Vibes ?? new List<CampVibe>(),
                        AcceptingMembers = season?.AcceptingMembers ?? YesNoMaybe.No,
                        KidsWelcome = season?.KidsWelcome ?? YesNoMaybe.No,
                        SoundZone = season?.SoundZone,
                        Status = season?.Status ?? CampSeasonStatus.Pending,
                        TimesAtNowhere = b.TimesAtNowhere
                    };
                }).ToList();
        }

        var pendingSeasons = await _campService.GetPendingSeasonsAsync();
        ViewBag.PendingCount = pendingSeasons.Count;

        var viewModel = new CampIndexViewModel
        {
            Year = year,
            Camps = cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            MyCamps = myCamps,
            Filters = filters ?? new CampFilterViewModel()
        };

        return View(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null)
            return NotFound();

        var settings = await _campService.GetSettingsAsync();
        var currentSeason = camp.Seasons
            .Where(s => s.Year == settings.PublicYear)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()
            ?? camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();

        var isLead = false;
        var isCampAdmin = false;
        var user = await GetCurrentUserAsync();

        if (user is not null)
        {
            isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
            isCampAdmin = RoleChecks.IsCampAdmin(User);
        }

        var viewModel = new CampDetailViewModel
        {
            Id = camp.Id,
            Slug = camp.Slug,
            Name = currentSeason?.Name ?? camp.Slug,
            ContactEmail = camp.ContactEmail,
            ContactMethod = camp.ContactMethod,
            WebOrSocialUrl = camp.WebOrSocialUrl,
            IsSwissCamp = camp.IsSwissCamp,
            TimesAtNowhere = camp.TimesAtNowhere,
            HistoricalNames = camp.HistoricalNames.Select(h => h.Name).ToList(),
            ImageUrls = camp.Images.OrderBy(i => i.SortOrder).Select(i => $"/{i.StoragePath}").ToList(),
            Leads = camp.Leads
                .Where(l => l.IsActive)
                .Select(l => new CampLeadViewModel
                {
                    LeadId = l.Id,
                    UserId = l.UserId,
                    DisplayName = l.User.DisplayName,
                }).ToList(),
            CurrentSeason = currentSeason != null ? MapSeasonDetail(currentSeason) : null,
            IsCurrentUserLead = isLead,
            IsCurrentUserCampAdmin = isCampAdmin
        };

        return View(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{slug}/Season/{year:int}")]
    public async Task<IActionResult> SeasonDetails(string slug, int year)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null)
            return NotFound();

        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null)
            return NotFound();

        var isLead = false;
        var isCampAdmin = false;
        var user = await GetCurrentUserAsync();

        if (user is not null)
        {
            isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
            isCampAdmin = RoleChecks.IsCampAdmin(User);
        }

        var viewModel = new CampDetailViewModel
        {
            Id = camp.Id,
            Slug = camp.Slug,
            Name = season.Name,
            ContactEmail = camp.ContactEmail,
            ContactMethod = camp.ContactMethod,
            WebOrSocialUrl = camp.WebOrSocialUrl,
            IsSwissCamp = camp.IsSwissCamp,
            TimesAtNowhere = camp.TimesAtNowhere,
            HistoricalNames = camp.HistoricalNames.Select(h => h.Name).ToList(),
            ImageUrls = camp.Images.OrderBy(i => i.SortOrder).Select(i => $"/{i.StoragePath}").ToList(),
            Leads = camp.Leads
                .Where(l => l.IsActive)
                .Select(l => new CampLeadViewModel
                {
                    LeadId = l.Id,
                    UserId = l.UserId,
                    DisplayName = l.User.DisplayName,
                }).ToList(),
            CurrentSeason = MapSeasonDetail(season),
            IsCurrentUserLead = isLead,
            IsCurrentUserCampAdmin = isCampAdmin
        };

        return View(nameof(Details), viewModel);
    }

    // ======================================================================
    // Registration
    // ======================================================================

    [Authorize]
    [HttpGet("Register")]
    public async Task<IActionResult> Register()
    {
        var settings = await _campService.GetSettingsAsync();
        if (settings.OpenSeasons.Count == 0)
        {
            TempData["ErrorMessage"] = "Registration is currently closed.";
            return RedirectToAction(nameof(Index));
        }

        var year = settings.OpenSeasons.OrderByDescending(y => y).FirstOrDefault();
        ViewData["SeasonYear"] = year;

        return View(new CampRegisterViewModel());
    }

    [Authorize]
    [HttpPost("Register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(CampRegisterViewModel model)
    {
        ValidatePhoneE164(model.ContactPhone, nameof(model.ContactPhone));

        if (!ModelState.IsValid)
            return View(model);

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var settings = await _campService.GetSettingsAsync();
        var year = settings.OpenSeasons.OrderByDescending(y => y).FirstOrDefault();
        if (year == 0)
        {
            TempData["ErrorMessage"] = "Registration is currently closed.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var historicalNames = string.IsNullOrWhiteSpace(model.HistoricalNames)
                ? null
                : model.HistoricalNames
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            var camp = await _campService.CreateCampAsync(
                user.Id,
                model.Name,
                model.ContactEmail,
                model.ContactPhone,
                model.WebOrSocialUrl,
                model.ContactMethod,
                model.IsSwissCamp,
                model.TimesAtNowhere,
                MapToSeasonData(model),
                historicalNames,
                year);

            TempData["SuccessMessage"] = "Your camp has been registered and is pending review.";
            return RedirectToAction(nameof(Details), new { slug = camp.Slug });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
    }

    // ======================================================================
    // Edit
    // ======================================================================

    [Authorize]
    [HttpGet("{slug}/Edit")]
    public async Task<IActionResult> Edit(string slug, int? year)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        var settings = await _campService.GetSettingsAsync();
        var preferredYear = year ?? settings.PublicYear;
        var season = camp.Seasons
            .Where(s => s.Year == preferredYear)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()
            ?? camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();

        if (season is null)
        {
            TempData["ErrorMessage"] = "No season found for this camp.";
            return RedirectToAction(nameof(Details), new { slug });
        }

        var viewModel = MapToEditViewModel(camp, season);
        return View(viewModel);
    }

    [Authorize]
    [HttpPost("{slug}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, CampEditViewModel model)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        ValidatePhoneE164(model.ContactPhone, nameof(model.ContactPhone));

        if (!ModelState.IsValid)
        {
            // Re-populate read-only fields
            var season = camp.Seasons.FirstOrDefault(s => s.Id == model.SeasonId);
            if (season is not null)
            {
                model.Leads = camp.Leads.Where(l => l.IsActive)
                    .Select(l => new CampLeadViewModel
                    {
                        LeadId = l.Id,
                        UserId = l.UserId,
                        DisplayName = l.User.DisplayName,
                    }).ToList();
                model.Images = camp.Images.OrderBy(i => i.SortOrder)
                    .Select(i => new CampImageViewModel
                    {
                        Id = i.Id,
                        Url = $"/{i.StoragePath}",
                        SortOrder = i.SortOrder
                    }).ToList();
            }
            return View(model);
        }

        try
        {
            await _campService.UpdateCampAsync(
                camp.Id,
                model.ContactEmail,
                model.ContactPhone,
                model.WebOrSocialUrl,
                model.ContactMethod,
                model.IsSwissCamp,
                model.TimesAtNowhere);

            await _campService.UpdateSeasonAsync(model.SeasonId, MapToSeasonData(model));

            // Handle name change if not locked
            var currentSeason = camp.Seasons.FirstOrDefault(s => s.Id == model.SeasonId);
            if (currentSeason is not null && !string.Equals(currentSeason.Name, model.Name, StringComparison.Ordinal))
            {
                var today = _clock.GetCurrentInstant().InUtc().Date;
                var nameLocked = currentSeason.NameLockDate.HasValue && today >= currentSeason.NameLockDate.Value;
                if (!nameLocked)
                {
                    await _campService.ChangeSeasonNameAsync(currentSeason.Id, model.Name);
                }
            }

            TempData["SuccessMessage"] = "Camp updated successfully.";
            return RedirectToAction(nameof(Edit), new { slug });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            var existingSeason = camp.Seasons.FirstOrDefault(s => s.Id == model.SeasonId);
            if (existingSeason is not null)
            {
                model = MapToEditViewModel(camp, existingSeason);
            }
            return View(model);
        }
    }

    // ======================================================================
    // Season opt-in
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/OptIn/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OptIn(string slug, int year)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.OptInToSeasonAsync(camp.Id, year);
            TempData["SuccessMessage"] = $"Opted in to season {year}.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug, year });
    }

    [Authorize]
    [HttpPost("{slug}/Withdraw/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(string slug, Guid seasonId)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.WithdrawSeasonAsync(seasonId);
            TempData["SuccessMessage"] = "Season withdrawn.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Rejoin/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rejoin(string slug, Guid seasonId)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.ReactivateSeasonAsync(seasonId);
            TempData["SuccessMessage"] = "Season reactivated. Welcome back!";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    // ======================================================================
    // Lead management
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/Leads/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLead(string slug, Guid userId)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        if (userId == Guid.Empty)
        {
            TempData["ErrorMessage"] = "Please search and select a human first.";
            return RedirectToAction(nameof(Edit), new { slug });
        }

        try
        {
            await _campService.AddLeadAsync(camp.Id, userId);
            TempData["SuccessMessage"] = "Co-lead added.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Leads/Remove/{leadId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLead(string slug, Guid leadId)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.RemoveLeadAsync(leadId);
            TempData["SuccessMessage"] = "Lead removed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }


    // ======================================================================
    // Image management
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/Images/Upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadImage(string slug, IFormFile file)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        if (file is null || file.Length == 0)
        {
            TempData["ErrorMessage"] = "Please select a file to upload.";
            return RedirectToAction(nameof(Edit), new { slug });
        }

        try
        {
            await _campService.UploadImageAsync(
                camp.Id,
                file.OpenReadStream(),
                file.FileName,
                file.ContentType,
                file.Length);
            TempData["SuccessMessage"] = "Image uploaded.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Images/Delete/{imageId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteImage(string slug, Guid imageId)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.DeleteImageAsync(imageId);
            TempData["SuccessMessage"] = "Image deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Images/Reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderImages(string slug, List<Guid> imageIds)
    {
        var camp = await _campService.GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id);
        var isCampAdmin = RoleChecks.IsCampAdmin(User);
        if (!isLead && !isCampAdmin) return Forbid();

        try
        {
            await _campService.ReorderImagesAsync(camp.Id, imageIds);
            TempData["SuccessMessage"] = "Image order updated.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    // ======================================================================
    // Helper methods
    // ======================================================================

    private static CampSeasonData MapToSeasonData(CampRegisterViewModel model)
    {
        return new CampSeasonData(
            BlurbLong: model.BlurbLong,
            BlurbShort: model.BlurbShort,
            Languages: model.Languages,
            AcceptingMembers: model.AcceptingMembers,
            KidsWelcome: model.KidsWelcome,
            KidsVisiting: model.KidsVisiting,
            KidsAreaDescription: model.KidsAreaDescription,
            HasPerformanceSpace: model.HasPerformanceSpace,
            PerformanceTypes: model.PerformanceTypes,
            Vibes: model.Vibes,
            AdultPlayspace: model.AdultPlayspace,
            MemberCount: model.MemberCount,
            SpaceRequirement: model.SpaceRequirement,
            SoundZone: model.SoundZone,
            ContainerCount: model.ContainerCount,
            ContainerNotes: model.ContainerNotes,
            ElectricalGrid: model.ElectricalGrid);
    }

    private CampEditViewModel MapToEditViewModel(Camp camp, CampSeason season)
    {
        var today = _clock.GetCurrentInstant().InUtc().Date;

        return new CampEditViewModel
        {
            CampId = camp.Id,
            Slug = camp.Slug,
            SeasonId = season.Id,
            Year = season.Year,
            IsNameLocked = season.NameLockDate.HasValue && today >= season.NameLockDate.Value,
            Name = season.Name,
            ContactEmail = camp.ContactEmail,
            ContactPhone = camp.ContactPhone,
            WebOrSocialUrl = camp.WebOrSocialUrl,
            ContactMethod = camp.ContactMethod,
            IsSwissCamp = camp.IsSwissCamp,
            TimesAtNowhere = camp.TimesAtNowhere,
            BlurbLong = season.BlurbLong,
            BlurbShort = season.BlurbShort,
            Languages = season.Languages,
            AcceptingMembers = season.AcceptingMembers,
            KidsWelcome = season.KidsWelcome,
            KidsVisiting = season.KidsVisiting,
            KidsAreaDescription = season.KidsAreaDescription,
            HasPerformanceSpace = season.HasPerformanceSpace,
            PerformanceTypes = season.PerformanceTypes,
            Vibes = season.Vibes.ToList(),
            AdultPlayspace = season.AdultPlayspace,
            MemberCount = season.MemberCount,
            SpaceRequirement = season.SpaceRequirement,
            SoundZone = season.SoundZone,
            ContainerCount = season.ContainerCount,
            ContainerNotes = season.ContainerNotes,
            ElectricalGrid = season.ElectricalGrid,
            Leads = camp.Leads
                .Where(l => l.IsActive)
                .Select(l => new CampLeadViewModel
                {
                    LeadId = l.Id,
                    UserId = l.UserId,
                    DisplayName = l.User.DisplayName,
                }).ToList(),
            Images = camp.Images
                .OrderBy(i => i.SortOrder)
                .Select(i => new CampImageViewModel
                {
                    Id = i.Id,
                    Url = $"/{i.StoragePath}",
                    SortOrder = i.SortOrder
                }).ToList()
        };
    }

    private CampSeasonDetailViewModel MapSeasonDetail(CampSeason season)
    {
        var today = _clock.GetCurrentInstant().InUtc().Date;

        return new CampSeasonDetailViewModel
        {
            Id = season.Id,
            Year = season.Year,
            Name = season.Name,
            Status = season.Status,
            BlurbLong = season.BlurbLong,
            BlurbShort = season.BlurbShort,
            Languages = season.Languages,
            AcceptingMembers = season.AcceptingMembers,
            KidsWelcome = season.KidsWelcome,
            KidsVisiting = season.KidsVisiting,
            KidsAreaDescription = season.KidsAreaDescription,
            HasPerformanceSpace = season.HasPerformanceSpace,
            PerformanceTypes = season.PerformanceTypes,
            Vibes = season.Vibes.ToList(),
            AdultPlayspace = season.AdultPlayspace,
            MemberCount = season.MemberCount,
            SpaceRequirement = season.SpaceRequirement,
            SoundZone = season.SoundZone,
            ContainerCount = season.ContainerCount,
            ContainerNotes = season.ContainerNotes,
            ElectricalGrid = season.ElectricalGrid,
            IsNameLocked = season.NameLockDate.HasValue && today >= season.NameLockDate.Value
        };
    }

    private void ValidatePhoneE164(string? phone, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(phone) && !phone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(fieldName,
                _localizer["Validation_PhoneE164", "Contact Phone"].Value);
        }
    }
}
