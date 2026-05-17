using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Domain.ValueObjects;
using Humans.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Web.Models.Camp;

namespace Humans.Web.Controllers;

[Route("Barrios")]
[Route("Camps")]
public class CampController : HumansCampControllerBase
{
    private readonly ICampService _campService;
    private readonly ICampContactService _campContactService;
    private readonly ICampRoleService _campRoleService;
    private readonly ICityPlanningService _cityPlanningService;
    private readonly INotificationService _notificationService;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly ILogger<CampController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CampController(
        ICampService campService,
        ICampContactService campContactService,
        ICampRoleService campRoleService,
        ICityPlanningService cityPlanningService,
        INotificationService notificationService,
        IUserService userService,
        IAuthorizationService authorizationService,
        IClock clock,
        ILogger<CampController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userService, campService, authorizationService)
    {
        _campService = campService;
        _campContactService = campContactService;
        _campRoleService = campRoleService;
        _cityPlanningService = cityPlanningService;
        _notificationService = notificationService;
        _userService = userService;
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
        var user = await GetCurrentUserInfoAsync();
        var directory = await _campService.GetCampDirectoryAsync(
            user?.Id,
            filters is null
                ? null
                : new CampDirectoryFilter(
                    filters.Vibe,
                    filters.SoundZone,
                    filters.KidsFriendly,
                    filters.AcceptingMembers,
                    filters.Search));

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
    public async Task<IActionResult> Details(string slug, CancellationToken cancellationToken)
    {
        var campDetail = await _campService.BuildCampDetailDataBySlugAsync(slug, cancellationToken: cancellationToken);
        if (campDetail is null)
            return NotFound();

        var currentUser = User.Identity?.IsAuthenticated == true ? await GetCurrentUserInfoAsync() : null;
        var (isLead, isCampAdmin) = await ResolveCampViewerStateAsync(campDetail.Id, currentUser, cancellationToken);
        var membership = await ResolveCurrentUserMembershipStateAsync(campDetail.Id, currentUser);
        await PopulateCityPlanningViewBagAsync(currentUser, cancellationToken);

        return View(MapCampDetailViewModel(campDetail, isLead, isCampAdmin, membership));
    }

    [AllowAnonymous]
    [HttpGet("{slug}/Season/{year:int}")]
    public async Task<IActionResult> SeasonDetails(string slug, int year, CancellationToken cancellationToken)
    {
        var campDetail = await _campService.BuildCampDetailDataBySlugAsync(
            slug,
            preferredYear: year,
            fallbackToLatestSeason: false,
            cancellationToken: cancellationToken);
        if (campDetail is null)
            return NotFound();

        var currentUser = User.Identity?.IsAuthenticated == true ? await GetCurrentUserInfoAsync() : null;
        var (isLead, isCampAdmin) = await ResolveCampViewerStateAsync(campDetail.Id, currentUser, cancellationToken);
        var membership = await ResolveCurrentUserMembershipStateAsync(campDetail.Id, currentUser);
        await PopulateCityPlanningViewBagAsync(currentUser, cancellationToken);

        return View(nameof(Details), MapCampDetailViewModel(campDetail, isLead, isCampAdmin, membership));
    }

    private async Task<CampMembershipStateViewModel> ResolveCurrentUserMembershipStateAsync(Guid campId, UserInfo? currentUser)
    {
        if (currentUser is null)
        {
            return new CampMembershipStateViewModel { Status = CampMemberStatusSummaryView.NoOpenSeason };
        }

        var state = await _campService.GetMembershipStateForCampAsync(campId, currentUser.Id);
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

        var currentUser = await GetCurrentUserInfoAsync();
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
        var senderEmail = currentUser.Email!;

        try
        {
            var result = await _campContactService.SendFacilitatedMessageAsync(
                camp.Id,
                camp.ContactEmail,
                campDisplayName,
                currentUser.Id,
                currentUser.BurnerName,
                senderEmail,
                model.Message,
                model.IncludeContactInfo,
                camp.Leads.Select(l => l.UserId).Distinct().ToList(),
                $"/Barrios/{slug}");

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
                null, // WebOrSocialUrl legacy � new registrations/edits use Links
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
        catch (DbUpdateException ex)
        {
            // Belt-and-suspenders for any future race in downstream sync side effects
            // (e.g. duplicate system-team memberships). The primary fix lives in the
            // owning service; this ensures the user always sees a friendly error
            // instead of a 500.
            _logger.LogError(ex, "Camp registration failed with DB error for user {UserId} in year {Year}", user.Id, year);
            ModelState.AddModelError(string.Empty, "We couldn't register your camp right now. Please try again, or contact an admin if the problem persists.");
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
    public async Task<IActionResult> Edit(string slug, int? year, CancellationToken ct)
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

        return View(viewModel);
    }

    [Authorize]
    [HttpGet("{slug}/Edit/Members")]
    public async Task<IActionResult> Members(string slug, int? year, CancellationToken ct)
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

        var openSeason = camp.Seasons.FirstOrDefault(s => s.Status == CampSeasonStatus.Active);
        viewModel.RolesPanel = openSeason is null
            ? null
            : await BuildRolesPanelAsync(camp.Slug, openSeason.Id, canManage: true, ct);

        return View(viewModel);
    }

    private async Task<CampRolesPanelViewModel> BuildRolesPanelAsync(
        string campSlug, Guid campSeasonId, bool canManage, CancellationToken ct)
    {
        var panelData = await _campRoleService.BuildPanelAsync(campSeasonId, ct);
        var members = await _campService.GetSeasonMembersAsync(campSeasonId, ct);
        var activeMemberUserIds = members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => m.UserId)
            .ToList();
        IReadOnlyDictionary<Guid, UserInfo> users = activeMemberUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await _userService.GetUserInfosAsync(activeMemberUserIds, ct);

        var activeMembers = members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => new CampMemberPickerOption(
                m.Id,
                m.UserId,
                users.TryGetValue(m.UserId, out var u) ? u.BurnerName : "(unknown)"))
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = panelData.Rows.Select(r => new CampRoleRowViewModel
        {
            DefinitionId = r.Definition.Id,
            Name = r.Definition.Name,
            Description = r.Definition.Description,
            SlotCount = r.Definition.SlotCount,
            MinimumRequired = r.Definition.MinimumRequired,
            FilledSlots = r.FilledSlots
                .Select(s => new CampRoleSlotViewModel(s.AssignmentId, s.CampMemberId, s.UserId, s.DisplayName))
                .ToList(),
            EmptySlotCount = r.EmptySlotCount,
            OverCapacity = r.OverCapacity,
            CurrentCount = r.CurrentCount,
        }).ToList();

        return new CampRolesPanelViewModel
        {
            CampSeasonId = campSeasonId,
            CampSlug = campSlug,
            CanManage = canManage,
            ActiveMembers = activeMembers,
            Rows = rows,
        };
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
                ConfirmedAt = m.ConfirmedAt,
                IsLead = m.IsLead,
                HasEarlyEntry = m.HasEarlyEntry,
                Status = m.Status
            })
            .ToList();
        viewModel.ActiveMembers = members.Active
            .Select(m => new CampMemberRowViewModel
            {
                CampMemberId = m.CampMemberId,
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                RequestedAt = m.RequestedAt,
                ConfirmedAt = m.ConfirmedAt,
                IsLead = m.IsLead,
                HasEarlyEntry = m.HasEarlyEntry,
                Status = m.Status
            })
            .ToList();
        viewModel.EeSlotCount = members.EeSlotCount;
        viewModel.EeGrantedCount = viewModel.ActiveMembers.Count(m => m.HasEarlyEntry);
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

        var result = await _campService.UpdateCampAsync(new CampUpdateInput(
            camp.Id,
            model.ContactEmail,
            model.ContactPhone,
            null, // WebOrSocialUrl legacy - new registrations/edits use Links
            ParseCampLinks(model.Links),
            model.IsSwissCamp,
            model.TimesAtNowhere,
            model.HideHistoricalNames,
            model.SeasonId,
            model.Name,
            MapToSeasonData(model)));

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Camp update failed.");
            await PopulateEditReadOnlyFieldsAsync(model);
            return View(model);
        }

        SetSuccess("Camp updated successfully.");
        return RedirectToAction(nameof(Edit), new { slug });
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
            return RedirectToAction(nameof(Members), new { slug });
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

        return RedirectToAction(nameof(Members), new { slug });
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

        return RedirectToAction(nameof(Members), new { slug });
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
    public async Task<IActionResult> UploadImage(string slug, IFormFile? file)
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

        var result = await _campService.UploadImageAsync(
            camp.Id,
            file.OpenReadStream(),
            file.FileName,
            file.ContentType,
            file.Length);

        if (result.Succeeded)
        {
            SetSuccess("Image uploaded.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Image upload failed.");
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
    // Camp membership per season (issue nobodies-collective#488)
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

        var result = await _campService.RequestCampMembershipAsync(camp.Id, user.Id);
        SetCampMemberRequestNotice(result);

        return RedirectToAction(nameof(Details), new { slug });
    }

    private void SetCampMemberRequestNotice(CampMemberRequestResult result)
    {
        if (result.NoticeLevel == CampMemberRequestNoticeLevel.Success)
        {
            SetSuccess(result.Message);
            return;
        }

        if (result.NoticeLevel == CampMemberRequestNoticeLevel.Info)
        {
            SetInfo(result.Message);
            return;
        }

        SetError(result.Message);
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

        var result = await _campService.LeaveCampAsync(campMemberId, user.Id);
        if (result.Succeeded)
        {
            SetSuccess("You have left this camp for this season.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Could not leave this camp.");
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
            // Scope by the authorized camp.Id � service rejects cross-camp member ids.
            await _campService.ApproveCampMemberAsync(camp.Id, campMemberId, user.Id);
            SetSuccess("Membership approved.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Approve camp membership failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/Reject/{campMemberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectMembership(string slug, Guid campMemberId)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        try
        {
            await _campService.RejectCampMemberAsync(camp.Id, campMemberId, user.Id);
            SetSuccess("Request rejected.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Reject camp membership failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
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
            await _campService.RemoveCampMemberAsync(camp.Id, campMemberId, user.Id);
            SetSuccess("Member removed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Remove camp member failed for member {MemberId} and camp {CampId}", campMemberId, camp.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [Authorize]
    [HttpPost("{slug}/Members/{campMemberId:guid}/EarlyEntry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMemberEarlyEntry(
        string slug, Guid campMemberId, bool granted, CancellationToken cancellationToken)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        var outcome = await _campService.SetEarlyEntryAsync(
            camp.Id, campMemberId, granted, user.Id, cancellationToken);

        if (outcome == SetEarlyEntryOutcome.MemberNotFound) return NotFound();
        ApplyEarlyEntryFlash(outcome, granted);
        return RedirectToAction(nameof(Members), new { slug });
    }

    private void ApplyEarlyEntryFlash(SetEarlyEntryOutcome outcome, bool granted)
    {
        if (outcome == SetEarlyEntryOutcome.Success)
            SetSuccess(granted ? "Early Entry granted." : "Early Entry revoked.");
        else if (outcome == SetEarlyEntryOutcome.SlotCapExceeded)
            SetError("Cannot grant Early Entry: slot cap reached for this camp.");
        else if (outcome == SetEarlyEntryOutcome.MemberNotActive)
            SetError("Only Active camp members can hold Early Entry.");
        // NoChange: silent � UI already reflected the state.
    }

    [Authorize]
    [HttpPost("{slug}/Members/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(string slug, Guid userId, CancellationToken ct)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        try
        {
            var result = await _campService.AddCampMemberToActiveSeasonAsLeadAsync(camp.Id, userId, user.Id, ct);
            if (result.Outcome == AddCampMemberAsLeadOutcome.InvalidUser)
                SetError("Please search and select a human first.");
            else if (result.Outcome == AddCampMemberAsLeadOutcome.NoActiveSeason)
                SetError("No active season for this camp.");
            else
                SetSuccess("Human added to camp.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddMember failed for camp {CampSlug}, user {UserId}.", slug, userId);
            SetError("Failed to add human to camp.");
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    // ======================================================================
    // Per-camp role assignments (issue nobodies-collective#489)
    // ======================================================================

    [Authorize]
    [HttpPost("{slug}/Roles/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRole(string slug, Guid roleDefinitionId, Guid campMemberId, CancellationToken ct)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        var openSeason = camp.Seasons.FirstOrDefault(s => s.Status == CampSeasonStatus.Active);
        var outcome = openSeason is null
            ? AssignCampRoleOutcome.SeasonNotFound
            : await _campRoleService.AssignAsync(openSeason.Id, roleDefinitionId, campMemberId, user.Id, ct);

        ApplyAssignRoleOutcomeFlash(outcome);

        return RedirectToAction(nameof(Members), new { slug });
    }

    /// <summary>
    /// Search-driven assign: takes a userId from the human-search typeahead. If the
    /// human isn't yet a camp member, they're added as Active first (idempotent), then
    /// the role is assigned. One UI action covers both "assign existing member" and
    /// "add this human and assign them" � see issue request from Frank, May 2026.
    /// </summary>
    [Authorize]
    [HttpPost("{slug}/Roles/AssignByUser")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRoleByUser(string slug, Guid roleDefinitionId, Guid userId, CancellationToken ct)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        if (userId == Guid.Empty)
        {
            SetError("Please search and select a human first.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        AssignCampRoleOutcome outcome;
        try
        {
            outcome = await _campService.AddMemberAndAssignRoleInActiveSeasonAsync(
                camp.Id, roleDefinitionId, userId, user.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssignRoleByUser failed for camp {CampSlug}, user {UserId}.", slug, userId);
            SetError("Failed to assign role.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        ApplyAssignRoleOutcomeFlash(outcome);

        return RedirectToAction(nameof(Members), new { slug });
    }

    private void ApplyAssignRoleOutcomeFlash(AssignCampRoleOutcome outcome)
    {
        var message = outcome switch
        {
            AssignCampRoleOutcome.Assigned => "Role assigned.",
            AssignCampRoleOutcome.RoleNotFound => "Role definition not found.",
            AssignCampRoleOutcome.RoleDeactivated => "That role is deactivated.",
            AssignCampRoleOutcome.MemberNotFound => "Camp member not found.",
            AssignCampRoleOutcome.MemberNotActive => "Only active camp members can hold roles.",
            AssignCampRoleOutcome.MemberSeasonMismatch => "Member is not in this season.",
            AssignCampRoleOutcome.SlotCapReached => "All slots for this role are filled.",
            AssignCampRoleOutcome.AlreadyHoldsRole => "That human already holds this role.",
            AssignCampRoleOutcome.SeasonNotFound => "Season not found.",
            _ => "Unknown error.",
        };

        if (outcome == AssignCampRoleOutcome.Assigned)
            SetSuccess(message);
        else
            SetError(message);
    }

    [Authorize]
    [HttpPost("{slug}/Roles/{assignmentId:guid}/Unassign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignRole(string slug, Guid assignmentId, CancellationToken ct)
    {
        var (errorResult, user, camp) = await ResolveCampManagementAsync(slug);
        if (errorResult is not null) return errorResult;

        // C2: verify the assignment belongs to a season of THIS camp before delegating to the service.
        var assignment = await _campRoleService.GetAssignmentByIdAsync(assignmentId, ct);
        var seasonIds = camp.Seasons.Select(s => s.Id).ToHashSet();
        if (assignment is null || !seasonIds.Contains(assignment.CampSeasonId))
        {
            _logger.LogWarning(
                "Cross-camp UnassignRole blocked: actor {ActorId} attempted assignment {AssignmentId} from camp {CampSlug}.",
                user.Id, assignmentId, slug);
            return Forbid();
        }

        var ok = await _campRoleService.UnassignAsync(assignmentId, user.Id, ct);
        if (ok)
        {
            SetSuccess("Role unassigned.");
        }
        else
        {
            SetError("Assignment not found.");
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    // ======================================================================
    // Helper methods
    // ======================================================================

    private async Task PopulateCityPlanningViewBagAsync(UserInfo? currentUser, CancellationToken cancellationToken)
    {
        if (currentUser is null)
        {
            return;
        }

        ViewBag.IsCityPlanningTeamMember =
            await _cityPlanningService.IsCityPlanningTeamMemberAsync(currentUser.Id, cancellationToken);

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        ViewBag.PlacementIsOpen = settings.IsPlacementOpen;
        ViewBag.PlacementOpensAt = settings.PlacementOpensAt;
        ViewBag.PlacementClosesAt = settings.PlacementClosesAt;
    }

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


