using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
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
    private readonly ICityPlanningService _cityPlanningService;
    private readonly INotificationService _notificationService;
    private readonly IClock _clock;
    private readonly ILogger<CampController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CampController(
        ICampService campService,
        ICampContactService campContactService,
        ICityPlanningService cityPlanningService,
        INotificationService notificationService,
        UserManager<User> userManager,
        IAuthorizationService authorizationService,
        IClock clock,
        ILogger<CampController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager, campService, authorizationService)
    {
        _campService = campService;
        _campContactService = campContactService;
        _cityPlanningService = cityPlanningService;
        _notificationService = notificationService;
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

    private async Task PopulateRegistrationInfoAsync()
    {
        ViewData["RegistrationInfo"] = await _cityPlanningService.GetRegistrationInfoAsync();
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
        var membership = await ResolveCurrentUserMembershipStateAsync(camp.Id);

        var detailViewModel = MapCampDetailViewModel(campDetail, isLead, isCampAdmin, membership);
        if (User.Identity?.IsAuthenticated == true)
        {
            detailViewModel.RolesPanel = await BuildRolesPanelAsync(camp.Id, HttpContext.RequestAborted);
        }
        return View(detailViewModel);
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
        var membership = await ResolveCurrentUserMembershipStateAsync(camp.Id);

        var detailViewModel = MapCampDetailViewModel(campDetail, isLead, isCampAdmin, membership);
        if (User.Identity?.IsAuthenticated == true)
        {
            detailViewModel.RolesPanel = await BuildRolesPanelAsync(camp.Id, HttpContext.RequestAborted);
        }
        return View(nameof(Details), detailViewModel);
    }

    private async Task<CampMembershipStateViewModel> ResolveCurrentUserMembershipStateAsync(Guid campId)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return new CampMembershipStateViewModel { Status = CampMemberStatusSummaryView.NoOpenSeason };
        }

        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return new CampMembershipStateViewModel { Status = CampMemberStatusSummaryView.NoOpenSeason };
        }

        var state = await _campService.GetMembershipStateForCampAsync(campId, user.Id);
        var status = state.Status switch
        {
            CampMemberStatusSummary.Active => CampMemberStatusSummaryView.Active,
            CampMemberStatusSummary.Pending => CampMemberStatusSummaryView.Pending,
            CampMemberStatusSummary.None => CampMemberStatusSummaryView.None,
            _ => CampMemberStatusSummaryView.NoOpenSeason
        };
        return new CampMembershipStateViewModel
        {
            OpenSeasonYear = state.OpenSeasonYear,
            CampMemberId = state.CampMemberId,
            Status = status
        };
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

            // In-app notification to camp leads (best-effort)
            try
            {
                var leadUserIds = camp.Leads
                    .Where(l => l.LeftAt == null)
                    .Select(l => l.UserId)
                    .Distinct()
                    .ToList();

                if (leadUserIds.Count > 0)
                {
                    await _notificationService.SendAsync(
                        NotificationSource.FacilitatedMessageReceived,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"New message for {campDisplayName} — check your email",
                        leadUserIds,
                        actionUrl: $"/Barrios/{slug}",
                        actionLabel: "View camp");
                }
            }
            catch (Exception notifEx)
            {
                _logger.LogError(notifEx, "Failed to dispatch FacilitatedMessageReceived notification for camp {Slug}", slug);
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

        await PopulateRegistrationInfoAsync();
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
            await PopulateRegistrationInfoAsync();
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
            await PopulateRegistrationInfoAsync();
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

        var viewModel = MapToEditViewModel(editData);
        await PopulateEditMembersAsync(viewModel);
        viewModel.RolesPanel = await BuildRolesPanelAsync(camp.Id, HttpContext.RequestAborted);
        return View(viewModel);
    }

    private async Task PopulateEditMembersAsync(CampEditViewModel viewModel)
    {
        if (viewModel.SeasonId == Guid.Empty)
        {
            return;
        }

        var members = await _campService.GetCampMembersAsync(viewModel.SeasonId);
        viewModel.PendingMembers = members.Pending
            .Select(m => new CampMemberRowViewModel
            {
                CampMemberId = m.CampMemberId,
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                RequestedAt = m.RequestedAt,
                ConfirmedAt = m.ConfirmedAt
            })
            .ToList();
        viewModel.ActiveMembers = members.Active
            .Select(m => new CampMemberRowViewModel
            {
                CampMemberId = m.CampMemberId,
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                RequestedAt = m.RequestedAt,
                ConfirmedAt = m.ConfirmedAt
            })
            .ToList();
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
    // Camp membership per season (issue #488)
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/Members/Request")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestMembership(string slug)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null) return currentUserError;

        try
        {
            var result = await _campService.RequestCampMembershipAsync(camp.Id, user.Id);
            switch (result.Outcome)
            {
                case CampMemberRequestOutcome.Created:
                    SetSuccess("Your request to join has been sent to the camp leads.");
                    break;
                case CampMemberRequestOutcome.AlreadyPending:
                    SetInfo("You already have a pending request for this camp.");
                    break;
                case CampMemberRequestOutcome.AlreadyActive:
                    SetInfo("You are already an active member of this camp.");
                    break;
                case CampMemberRequestOutcome.NoOpenSeason:
                    SetError(result.Message ?? "Camp is not open for membership this year.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Camp membership request failed for camp {CampId} and user {UserId}", camp.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Withdraw/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawMembershipRequest(string slug, Guid campMemberId)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null) return currentUserError;

        try
        {
            await _campService.WithdrawCampMembershipRequestAsync(campMemberId, user.Id);
            SetSuccess("Your pending request was withdrawn.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Withdraw camp membership request failed for member {MemberId} and user {UserId}", campMemberId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Leave/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LeaveMembership(string slug, Guid campMemberId)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null) return NotFound();

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null) return currentUserError;

        try
        {
            await _campService.LeaveCampAsync(campMemberId, user.Id);
            SetSuccess("You have left this camp for this season.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Leave camp failed for member {MemberId} and user {UserId}", campMemberId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Details), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Approve/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveMembership(string slug, Guid campMemberId)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        try
        {
            await _campService.ApproveCampMemberAsync(campMemberId, user.Id);

            var member = await _campService.GetCampMembersAsync(await ResolveOpenSeasonIdForCampAsync(camp.Id));
            var row = member.Active.FirstOrDefault(r => r.CampMemberId == campMemberId)
                ?? member.Pending.FirstOrDefault(r => r.CampMemberId == campMemberId);

            // Notify the requester (best-effort)
            try
            {
                var campName = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name ?? camp.Slug;
                if (row is not null)
                {
                    await _notificationService.SendAsync(
                        NotificationSource.CampMembershipApproved,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"Your request to join {campName} was approved",
                        [row.UserId],
                        actionUrl: $"/Camps/{slug}",
                        actionLabel: "View camp");
                }
            }
            catch (Exception notifEx)
            {
                _logger.LogError(notifEx, "Failed to notify requester about approved camp membership {MemberId}", campMemberId);
            }

            SetSuccess("Membership approved.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Approve camp membership failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Reject/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectMembership(string slug, Guid campMemberId)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        // Capture the requester BEFORE rejecting (after rejection the row status is Removed).
        var seasonId = await ResolveOpenSeasonIdForCampAsync(camp.Id);
        Guid? requesterUserId = null;
        if (seasonId != Guid.Empty)
        {
            var list = await _campService.GetCampMembersAsync(seasonId);
            requesterUserId = list.Pending.FirstOrDefault(r => r.CampMemberId == campMemberId)?.UserId;
        }

        try
        {
            await _campService.RejectCampMemberAsync(campMemberId, user.Id);

            try
            {
                if (requesterUserId.HasValue)
                {
                    var campName = camp.Seasons.OrderByDescending(s => s.Year).FirstOrDefault()?.Name ?? camp.Slug;
                    await _notificationService.SendAsync(
                        NotificationSource.CampMembershipRejected,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"Your request to join {campName} was not approved",
                        [requesterUserId.Value]);
                }
            }
            catch (Exception notifEx)
            {
                _logger.LogError(notifEx, "Failed to notify requester about rejected camp membership {MemberId}", campMemberId);
            }

            SetSuccess("Request rejected.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Reject camp membership failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Remove/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMembership(string slug, Guid campMemberId)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        try
        {
            await _campService.RemoveCampMemberAsync(campMemberId, user.Id);
            SetSuccess("Member removed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Remove camp member failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    private async Task<Guid> ResolveOpenSeasonIdForCampAsync(Guid campId)
    {
        var settings = await _campService.GetSettingsAsync();
        var camp = await _campService.GetCampByIdAsync(campId);
        var season = camp?.Seasons
            .FirstOrDefault(s => s.Year == settings.PublicYear
                && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full));
        return season?.Id ?? Guid.Empty;
    }

    // ======================================================================
    // Camp role assignments (per-camp, lead-managed)
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/Roles/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRole(
        string slug,
        Guid campRoleDefinitionId,
        int slotIndex,
        Guid assigneeUserId,
        bool autoPromote,
        CancellationToken cancellationToken)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        if (assigneeUserId == Guid.Empty)
        {
            SetError("Please search and select a human first.");
            return RedirectToAction(nameof(Edit), new { slug });
        }

        var seasonId = await ResolveOpenSeasonIdForCampAsync(camp.Id);
        if (seasonId == Guid.Empty)
        {
            SetError("Camp has no open season.");
            return RedirectToAction(nameof(Edit), new { slug });
        }

        var result = await _campService.AssignCampRoleAsync(
            seasonId, campRoleDefinitionId, slotIndex, assigneeUserId, user.Id, autoPromote, cancellationToken);

        if (result.Outcome is AssignCampRoleOutcome.Assigned or AssignCampRoleOutcome.AssignedWithAutoPromote)
        {
            if (result.Outcome == AssignCampRoleOutcome.AssignedWithAutoPromote && result.AssigneeUserId.HasValue)
            {
                try
                {
                    await _notificationService.SendAsync(
                        NotificationSource.CampMembershipApproved,
                        NotificationClass.Actionable,
                        NotificationPriority.Normal,
                        $"You're now a member of {result.CampName}",
                        [result.AssigneeUserId.Value],
                        body: $"You've been added as an active member of {result.CampName} for the current season.",
                        actionUrl: $"/Camps/{result.CampSlug}",
                        actionLabel: "View barrio",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send CampMembershipApproved notification for camp {Slug}", slug);
                }
            }

            if (result.AssigneeUserId.HasValue)
            {
                try
                {
                    await _notificationService.SendAsync(
                        NotificationSource.CampRoleAssigned,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"You're now {result.RoleName} for {result.CampName}",
                        [result.AssigneeUserId.Value],
                        body: $"You've been assigned as {result.RoleName} for the current season at {result.CampName}.",
                        actionUrl: $"/Camps/{result.CampSlug}",
                        actionLabel: "View barrio",
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send CampRoleAssigned notification for camp {Slug}", slug);
                }
            }

            SetSuccess(result.Outcome == AssignCampRoleOutcome.AssignedWithAutoPromote
                ? "Member added to barrio and assigned to role."
                : "Role assigned.");
        }
        else
        {
            SetError(result.ErrorMessage ?? result.Outcome.ToString());
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Roles/{assignmentId:guid}/Unassign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignRole(string slug, Guid assignmentId, CancellationToken cancellationToken)
    {
        var (errorResult, user, _) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        try
        {
            await _campService.UnassignCampRoleAsync(assignmentId, user.Id, cancellationToken);
            SetSuccess("Role unassigned.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Unassign camp role failed for assignment {AssignmentId} in camp {Slug}", assignmentId, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Edit), new { slug });
    }

    private async Task<CampRolesPanelViewModel> BuildRolesPanelAsync(Guid campId, CancellationToken cancellationToken)
    {
        var seasonId = await ResolveOpenSeasonIdForCampAsync(campId);
        if (seasonId == Guid.Empty)
        {
            return new CampRolesPanelViewModel { HasOpenSeason = false };
        }

        var defs = await _campService.GetCampRoleDefinitionsAsync(includeDeactivated: false, cancellationToken);
        var assignments = await _campService.GetCampRoleAssignmentsAsync(seasonId, cancellationToken);

        var rows = defs
            .OrderBy(d => d.SortOrder)
            .Select(d =>
            {
                var slots = new List<CampRoleSlotViewModel>();
                var perDefAssignments = assignments
                    .Where(a => a.CampRoleDefinitionId == d.Id)
                    .ToList();

                for (var i = 0; i < d.SlotCount; i++)
                {
                    var hit = perDefAssignments.FirstOrDefault(a => a.SlotIndex == i);
                    slots.Add(new CampRoleSlotViewModel
                    {
                        SlotIndex = i,
                        AssignmentId = hit?.AssignmentId,
                        AssigneeUserId = hit?.AssigneeUserId,
                        AssigneeDisplayName = hit?.AssigneeDisplayName,
                        IsBeingPhasedOut = false
                    });
                }

                // Orphan high-slot rows (when SlotCount was lowered after assignment)
                foreach (var orphan in perDefAssignments.Where(a => a.SlotIndex >= d.SlotCount))
                {
                    slots.Add(new CampRoleSlotViewModel
                    {
                        SlotIndex = orphan.SlotIndex,
                        AssignmentId = orphan.AssignmentId,
                        AssigneeUserId = orphan.AssigneeUserId,
                        AssigneeDisplayName = orphan.AssigneeDisplayName,
                        IsBeingPhasedOut = true
                    });
                }

                return new CampRoleRowViewModel
                {
                    CampRoleDefinitionId = d.Id,
                    Name = d.Name,
                    IsRequired = d.IsRequired,
                    SlotCount = d.SlotCount,
                    MinimumRequired = d.MinimumRequired,
                    Slots = slots
                };
            })
            .ToList();

        return new CampRolesPanelViewModel
        {
            CampSeasonId = seasonId,
            Roles = rows,
            HasOpenSeason = true
        };
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
        await PopulateEditMembersAsync(model);
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
        bool isCampAdmin,
        CampMembershipStateViewModel membership) => new()
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
            IsCurrentUserCampAdmin = isCampAdmin,
            Membership = membership
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
