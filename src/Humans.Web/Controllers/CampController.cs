using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Helpers;
using Humans.Domain.ValueObjects;
using Humans.Web.Models;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Web.Controllers;

[Route("Barrios")]
[Route("Camps")]
public class CampController : HumansCampControllerBase
{
    private readonly ICampService _campService;
    private readonly ICampContactService _campContactService;
    private readonly IClock _clock;
    private readonly ILogger<CampController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CampController(
        ICampService campService,
        ICampContactService campContactService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<CampController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager, campService)
    {
        _campService = campService;
        _campContactService = campContactService;
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
        var user = await GetCurrentUserAsync();
        var directory = await _campService.GetCampDirectoryAsync(
            user?.Id,
            filters is null
                ? null
                : new CampDirectoryFilter(
                    filters.Vibe,
                    filters.SoundZone,
                    filters.KidsFriendly,
                    filters.AcceptingMembers));

        ViewBag.PendingCount = directory.PendingCount;

        var viewModel = new CampIndexViewModel
        {
            Year = directory.Year,
            Camps = directory.Camps.Select(MapCampCard).ToList(),
            MyCamps = directory.MyCamps.Select(MapCampCard).ToList(),
            Filters = filters ?? new CampFilterViewModel()
        };

        return View(viewModel);
    }

    private static CampCardViewModel MapCampCard(CampDirectoryCard card) => new()
    {
        Id = card.Id,
        Slug = card.Slug,
        Name = card.Name,
        BlurbShort = card.BlurbShort,
        ImageUrl = card.ImageUrl,
        Vibes = [.. card.Vibes],
        AcceptingMembers = card.AcceptingMembers,
        KidsWelcome = card.KidsWelcome,
        SoundZone = card.SoundZone,
        Status = card.Status,
        TimesAtNowhere = card.TimesAtNowhere
    };

    private async Task PopulateRegisterSeasonYearAsync()
    {
        var settings = await _campService.GetSettingsAsync();
        ViewData["SeasonYear"] = settings.OpenSeasons.OrderByDescending(y => y).FirstOrDefault();
    }

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var campDetail = await _campService.GetCampDetailAsync(slug);
        if (campDetail is null)
            return NotFound();

        var camp = await GetCampBySlugAsync(slug);
        if (camp is null)
            return NotFound();

        var (isLead, isCampAdmin) = await ResolveCampViewerStateAsync(camp);

        return View(MapCampDetailViewModel(campDetail, isLead, isCampAdmin));
    }

    [AllowAnonymous]
    [HttpGet("{slug}/Season/{year:int}")]
    public async Task<IActionResult> SeasonDetails(string slug, int year)
    {
        var campDetail = await _campService.GetCampDetailAsync(
            slug,
            preferredYear: year,
            fallbackToLatestSeason: false);
        if (campDetail is null)
            return NotFound();

        var camp = await GetCampBySlugAsync(slug);
        if (camp is null)
            return NotFound();

        var (isLead, isCampAdmin) = await ResolveCampViewerStateAsync(camp);

        return View(nameof(Details), MapCampDetailViewModel(campDetail, isLead, isCampAdmin));
    }

    // ======================================================================
    // Facilitated Contact
    // ======================================================================

    [Authorize]
    [HttpGet("{slug}/Contact")]
    public async Task<IActionResult> Contact(string slug)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var season = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
        var model = new CampContactViewModel
        {
            CampSlug = slug,
            CampName = season?.Name ?? slug
        };
        return View(model);
    }

    [Authorize]
    [HttpPost("{slug}/Contact")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Contact(string slug, CampContactViewModel model)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        if (!ModelState.IsValid)
        {
            model.CampSlug = slug;
            var season = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault();
            model.CampName = season?.Name ?? slug;
            return View(model);
        }

        var campDisplayName = camp.Seasons
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()?.Name ?? slug;
        var senderEmail = currentUser.GetEffectiveEmail() ?? currentUser.Email!;

        try
        {
            var result = await _campContactService.SendFacilitatedMessageAsync(
                camp.Id,
                camp.ContactEmail,
                campDisplayName,
                currentUser.Id,
                currentUser.DisplayName,
                senderEmail,
                model.Message,
                model.IncludeContactInfo);

            if (result.RateLimited)
            {
                SetError(_localizer["Camp_Contact_RateLimited"].Value);
                return RedirectToAction(nameof(Details), new { slug });
            }

            SetSuccess(string.Format(_localizer["Camp_Contact_Success"].Value, campDisplayName));
            return RedirectToAction(nameof(Details), new { slug });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send facilitated message to camp {Slug}", slug);
            SetError(_localizer["Common_Error"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }
    }

    // ======================================================================
    // Registration
    // ======================================================================

    [Authorize]
    [HttpGet("Register")]
    public async Task<IActionResult> Register()
    {
        await PopulateRegisterSeasonYearAsync();
        if ((int?)ViewData["SeasonYear"] == 0)
        {
            SetError("Registration is currently closed.");
            return RedirectToAction(nameof(Index));
        }

        return View(new CampRegisterViewModel());
    }

    [Authorize]
    [HttpPost("Register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(CampRegisterViewModel model)
    {
        ValidatePhoneE164(model.ContactPhone, nameof(model.ContactPhone));

        if (!ModelState.IsValid)
        {
            await PopulateRegisterSeasonYearAsync();
            return View(model);
        }

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var settings = await _campService.GetSettingsAsync();
        var year = settings.OpenSeasons.OrderByDescending(y => y).FirstOrDefault();
        if (year == 0)
        {
            SetError("Registration is currently closed.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var historicalNames = string.IsNullOrWhiteSpace(model.HistoricalNames)
                ? null
                : model.HistoricalNames
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            var campLinks = ParseCampLinks(model.Links);

            var camp = await _campService.CreateCampAsync(
                user.Id,
                model.Name,
                model.ContactEmail,
                model.ContactPhone,
                null, // WebOrSocialUrl legacy — new registrations/edits use Links
                campLinks,
                model.IsSwissCamp,
                model.TimesAtNowhere,
                MapToSeasonData(model),
                historicalNames,
                year);

            SetSuccess("Your camp has been registered and is pending review.");
            return RedirectToAction(nameof(Details), new { slug = camp.Slug });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp registration failed for user {UserId} in year {Year}", user.Id, year);
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateRegisterSeasonYearAsync();
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
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var editData = await _campService.GetCampEditDataAsync(camp.Id, year);
        if (editData is null)
        {
            SetError("No season found for this camp.");
            return RedirectToAction(nameof(Details), new { slug });
        }

        return View(MapToEditViewModel(editData));
    }

    [Authorize]
    [HttpPost("{slug}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, CampEditViewModel model)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        ValidatePhoneE164(model.ContactPhone, nameof(model.ContactPhone));

        if (!ModelState.IsValid)
        {
            await PopulateEditReadOnlyFieldsAsync(model);
            return View(model);
        }

        try
        {
            var updateLinks = ParseCampLinks(model.Links);

            await _campService.UpdateCampAsync(
                camp.Id,
                model.ContactEmail,
                model.ContactPhone,
                null, // WebOrSocialUrl legacy — new registrations/edits use Links
                updateLinks,
                model.IsSwissCamp,
                model.TimesAtNowhere,
                model.HideHistoricalNames);

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

            SetSuccess("Camp updated successfully.");
            return RedirectToAction(nameof(Edit), new { slug });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp update failed for camp {CampId} and slug {Slug}", camp.Id, slug);
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateEditReadOnlyFieldsAsync(model);
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
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.OptInToSeasonAsync(camp.Id, year);
            SetSuccess($"Opted in to season {year}.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp opt-in failed for camp {CampId}, slug {Slug}, and year {Year}", camp.Id, slug, year);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug, year });
    }

    [Authorize]
    [HttpPost("{slug}/Withdraw/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(string slug, Guid seasonId)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.WithdrawSeasonAsync(seasonId);
            SetSuccess("Season withdrawn.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp season withdrawal failed for camp {CampId}, slug {Slug}, and season {SeasonId}", camp.Id, slug, seasonId);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Rejoin/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rejoin(string slug, Guid seasonId)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.ReactivateSeasonAsync(seasonId);
            SetSuccess("Season reactivated. Welcome back!");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp season reactivation failed for camp {CampId}, slug {Slug}, and season {SeasonId}", camp.Id, slug, seasonId);
            SetError(ex.Message);
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
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        if (userId == Guid.Empty)
        {
            SetError("Please search and select a human first.");
            return RedirectToAction(nameof(Edit), new { slug });
        }

        try
        {
            await _campService.AddLeadAsync(camp.Id, userId);
            SetSuccess("Co-lead added.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Adding lead {LeadUserId} failed for camp {CampId} and slug {Slug}", userId, camp.Id, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Leads/Remove/{leadId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLead(string slug, Guid leadId)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.RemoveLeadAsync(leadId);
            SetSuccess("Lead removed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Removing lead {LeadId} failed for camp {CampId} and slug {Slug}", leadId, camp.Id, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }


    // ======================================================================
    // Historical name management
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/HistoricalNames/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHistoricalName(string slug, string name)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
            return errorResult;

        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("Name cannot be empty.");
            return RedirectToAction(nameof(Edit), new { slug });
        }

        try
        {
            await _campService.AddHistoricalNameAsync(camp.Id, name);
            SetSuccess("Historical name added.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Adding historical name failed for camp {CampId}", camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/HistoricalNames/Remove/{nameId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveHistoricalName(string slug, Guid nameId)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
            return errorResult;

        try
        {
            await _campService.RemoveHistoricalNameAsync(nameId);
            SetSuccess("Historical name removed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Removing historical name {NameId} failed for camp {CampId}", nameId, camp.Id);
            SetError(ex.Message);
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
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        if (file is null || file.Length == 0)
        {
            SetError("Please select a file to upload.");
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
            SetSuccess("Image uploaded.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Image upload failed for camp {CampId} and slug {Slug}", camp.Id, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Images/Delete/{imageId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteImage(string slug, Guid imageId)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.DeleteImageAsync(imageId);
            SetSuccess("Image deleted.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Deleting image {ImageId} failed for camp {CampId} and slug {Slug}", imageId, camp.Id, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Images/Reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderImages(string slug, List<Guid> imageIds)
    {
        var (errorResult, _, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null)
        {
            return errorResult;
        }

        try
        {
            await _campService.ReorderImagesAsync(camp.Id, imageIds);
            SetSuccess("Image order updated.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Reordering images failed for camp {CampId} and slug {Slug}", camp.Id, slug);
            SetError(ex.Message);
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

    private async Task PopulateEditReadOnlyFieldsAsync(CampEditViewModel model)
    {
        var editData = await _campService.GetCampEditDataAsync(model.CampId, model.Year);
        if (editData is null)
        {
            model.Leads = [];
            model.Images = [];
            return;
        }

        model.Leads = editData.Leads
            .Select(lead => new CampLeadViewModel
            {
                LeadId = lead.LeadId,
                UserId = lead.UserId,
                DisplayName = lead.DisplayName
            })
            .ToList();
        model.Images = editData.Images
            .Select(image => new CampImageViewModel
            {
                Id = image.Id,
                Url = image.Url,
                SortOrder = image.SortOrder
            })
            .ToList();
    }

    private static CampEditViewModel MapToEditViewModel(CampEditData editData) =>
        new()
        {
            CampId = editData.CampId,
            Slug = editData.Slug,
            SeasonId = editData.SeasonId,
            Year = editData.Year,
            IsNameLocked = editData.IsNameLocked,
            Name = editData.Name,
            ContactEmail = editData.ContactEmail,
            ContactPhone = editData.ContactPhone,
            Links = [.. editData.Links],
            IsSwissCamp = editData.IsSwissCamp,
            HideHistoricalNames = editData.HideHistoricalNames,
            TimesAtNowhere = editData.TimesAtNowhere,
            BlurbLong = editData.BlurbLong,
            BlurbShort = editData.BlurbShort,
            Languages = editData.Languages,
            AcceptingMembers = editData.AcceptingMembers,
            KidsWelcome = editData.KidsWelcome,
            KidsVisiting = editData.KidsVisiting,
            KidsAreaDescription = editData.KidsAreaDescription,
            HasPerformanceSpace = editData.HasPerformanceSpace,
            PerformanceTypes = editData.PerformanceTypes,
            Vibes = [.. editData.Vibes],
            AdultPlayspace = editData.AdultPlayspace,
            MemberCount = editData.MemberCount,
            SpaceRequirement = editData.SpaceRequirement,
            SoundZone = editData.SoundZone,
            ContainerCount = editData.ContainerCount,
            ContainerNotes = editData.ContainerNotes,
            ElectricalGrid = editData.ElectricalGrid,
            Leads = editData.Leads
                .Select(lead => new CampLeadViewModel
                {
                    LeadId = lead.LeadId,
                    UserId = lead.UserId,
                    DisplayName = lead.DisplayName
                }).ToList(),
            Images = editData.Images
                .Select(image => new CampImageViewModel
                {
                    Id = image.Id,
                    Url = image.Url,
                    SortOrder = image.SortOrder
                }).ToList(),
            ExistingHistoricalNames = editData.HistoricalNames
                .Select(h => new CampHistoricalNameViewModel
                {
                    Id = h.Id,
                    Name = h.Name,
                    Year = h.Year,
                    Source = h.Source
                }).ToList()
        };

    private static CampDetailViewModel MapCampDetailViewModel(
        CampDetailData campDetail,
        bool isLead,
        bool isCampAdmin) => new()
        {
            Id = campDetail.Id,
            Slug = campDetail.Slug,
            Name = campDetail.Name,
            Links = [.. campDetail.Links],
            IsSwissCamp = campDetail.IsSwissCamp,
            HideHistoricalNames = campDetail.HideHistoricalNames,
            TimesAtNowhere = campDetail.TimesAtNowhere,
            HistoricalNames = [.. campDetail.HistoricalNames],
            ImageUrls = [.. campDetail.ImageUrls],
            Leads = campDetail.Leads
            .Select(lead => new CampLeadViewModel
            {
                LeadId = lead.LeadId,
                UserId = lead.UserId,
                DisplayName = lead.DisplayName
            }).ToList(),
            CurrentSeason = campDetail.CurrentSeason is null
            ? null
            : new CampSeasonDetailViewModel
            {
                Id = campDetail.CurrentSeason.Id,
                Year = campDetail.CurrentSeason.Year,
                Name = campDetail.CurrentSeason.Name,
                Status = campDetail.CurrentSeason.Status,
                BlurbLong = campDetail.CurrentSeason.BlurbLong,
                BlurbShort = campDetail.CurrentSeason.BlurbShort,
                Languages = campDetail.CurrentSeason.Languages,
                AcceptingMembers = campDetail.CurrentSeason.AcceptingMembers,
                KidsWelcome = campDetail.CurrentSeason.KidsWelcome,
                KidsVisiting = campDetail.CurrentSeason.KidsVisiting,
                KidsAreaDescription = campDetail.CurrentSeason.KidsAreaDescription,
                HasPerformanceSpace = campDetail.CurrentSeason.HasPerformanceSpace,
                PerformanceTypes = campDetail.CurrentSeason.PerformanceTypes,
                Vibes = [.. campDetail.CurrentSeason.Vibes],
                AdultPlayspace = campDetail.CurrentSeason.AdultPlayspace,
                MemberCount = campDetail.CurrentSeason.MemberCount,
                SpaceRequirement = campDetail.CurrentSeason.SpaceRequirement,
                SoundZone = campDetail.CurrentSeason.SoundZone,
                ContainerCount = campDetail.CurrentSeason.ContainerCount,
                ContainerNotes = campDetail.CurrentSeason.ContainerNotes,
                ElectricalGrid = campDetail.CurrentSeason.ElectricalGrid,
                IsNameLocked = campDetail.CurrentSeason.IsNameLocked
            },
            IsCurrentUserLead = isLead,
            IsCurrentUserCampAdmin = isCampAdmin
        };

    private void ValidatePhoneE164(string? phone, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(phone) && !phone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(fieldName,
                _localizer["Validation_PhoneE164", "Contact Phone"].Value);
        }
    }

    private static List<CampLink>? ParseCampLinks(IEnumerable<string?> links)
    {
        var parsedLinks = links
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .Select(link => link!.Trim())
            .Where(IsHttpUrl)
            .Select(link => new CampLink
            {
                Url = link,
                Platform = PlatformDetector.Detect(link).Name
            })
            .ToList();

        return parsedLinks.Count > 0 ? parsedLinks : null;
    }

    private static bool IsHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
    }
}
