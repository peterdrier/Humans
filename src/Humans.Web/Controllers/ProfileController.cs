// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Humans.Application.Authorization;
using Humans.Application.Authorization.UserEmail;
using Humans.Application.Configuration;
using Humans.Application.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Gdpr;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;

// RoleAssignment cross-domain nav properties (User, CreatedByUser) are [Obsolete] —
// RoleAssignmentService stitches them in memory from IUserService so controllers can
// continue to read them for view-model shaping. Nav-strip follow-up tracked in
// design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Authorize]
[Route("Profile")]
public class ProfileController : HumansControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IEmailService _emailService;
    private readonly IUserEmailService _userEmailService;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly IHumanLifecycleService _humanLifecycleService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IGdprExportService _gdprExportService;
    private readonly IConfiguration _configuration;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly ILogger<ProfileController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ITeamService _teamService;
    private readonly ICampaignService _campaignService;
    private readonly IEmailOutboxService _emailOutboxService;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly IAuthorizationService _authorizationService;
    private readonly IUserService _userService;
    private readonly IConsentService _consentService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SignInManager<User> _signInManager;
    private readonly GoogleWorkspaceOptions _googleWorkspaceOptions;

    private const int MaxProfilePictureUploadBytes = 20 * 1024 * 1024; // 20MB upload limit
    private const int MaxGooglePhotoDownloadBytes = 20 * 1024 * 1024; // 20MB hard ceiling for Google avatar fetch
    private const string GoogleAvatarHttpClientName = "GoogleAvatar";
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/heic",
        "image/heif",
        "image/avif"
    };
    private static readonly Dictionary<string, string> HeifExtensionToContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".avif"] = "image/avif"
    };
    private static readonly System.Text.Json.JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ProfileController(
        IUserService userService,
        UserManager<User> userManager,
        IProfileService profileService,
        IContactFieldService contactFieldService,
        IEmailService emailService,
        IUserEmailService userEmailService,
        ICommunicationPreferenceService commPrefService,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        IHumanLifecycleService humanLifecycleService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftMgmt,
        IGdprExportService gdprExportService,
        IConfiguration configuration,
        ConfigurationRegistry configRegistry,
        ILogger<ProfileController> logger,
        IStringLocalizer<SharedResource> localizer,
        ITicketQueryService ticketQueryService,
        ITeamService teamService,
        ICampaignService campaignService,
        IEmailOutboxService emailOutboxService,
        IMemoryCache cache,
        IClock clock,
        IAuthorizationService authorizationService,
        IConsentService consentService,
        IApplicationDecisionService applicationDecisionService,
        IAccountDeletionService accountDeletionService,
        IMembershipCalculator membershipCalculator,
        IHttpClientFactory httpClientFactory,
        SignInManager<User> signInManager,
        IOptions<GoogleWorkspaceOptions> googleWorkspaceOptions)
        : base(userService)
    {
        _userManager = userManager;
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _emailService = emailService;
        _userEmailService = userEmailService;
        _commPrefService = commPrefService;
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _humanLifecycleService = humanLifecycleService;
        _roleAssignmentService = roleAssignmentService;
        _shiftSignupService = shiftSignupService;
        _shiftMgmt = shiftMgmt;
        _gdprExportService = gdprExportService;
        _configuration = configuration;
        _configRegistry = configRegistry;
        _logger = logger;
        _localizer = localizer;
        _ticketQueryService = ticketQueryService;
        _teamService = teamService;
        _campaignService = campaignService;
        _emailOutboxService = emailOutboxService;
        _cache = cache;
        _clock = clock;
        _authorizationService = authorizationService;
        _userService = userService;
        _consentService = consentService;
        _applicationDecisionService = applicationDecisionService;
        _accountDeletionService = accountDeletionService;
        _membershipCalculator = membershipCalculator;
        _httpClientFactory = httpClientFactory;
        _signInManager = signInManager;
        _googleWorkspaceOptions = googleWorkspaceOptions.Value;
    }

    /// <summary>
    /// Composes the admin humans list: optional text-filter via the
    /// <see cref="PersonSearchFields.AdminAll"/> bit-flag search,
    /// status-partition lookup, and projection to <see cref="AdminHumanRow"/>.
    /// Replaces the deleted <c>IProfileService.GetFilteredHumansAsync</c>.
    /// Pure controller-layer composition — no business logic, just data
    /// orchestration.
    /// </summary>
    private async Task<IReadOnlyList<AdminHumanRow>> BuildAdminHumansAsync(
        string? search, string? statusFilter, CancellationToken ct)
    {
        var allUsers = await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = allUsers.Select(u => u.Id).ToList();
        var notificationEmails =
            await _userEmailService.GetNotificationEmailsByUserIdsAsync(allUserIds, ct);

        IReadOnlySet<Guid>? searchUserIds = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Admin auth is enforced by the action's [Authorize] policy, so
            // PersonSearchFields.AdminAll is appropriate here. Limit large
            // enough that "show me every match" is the practical effect at
            // ~500-user scale; the controller paginates afterward.
            var searchResults = await _userService.SearchUsersAsync(
                search, PersonSearchFields.AdminAll, limit: 500, ct);

            // Email-direct match isn't covered by the matcher's verified-emails
            // bucket on User.Email (only UserEmail rows). Union the two so
            // existing-data parity is preserved.
            var byEmail = allUsers
                .Where(u =>
                    (u.Email ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    u.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(u => u.Id);

            searchUserIds = searchResults
                .Select(r => r.UserId)
                .Concat(byEmail)
                .ToHashSet();
        }

        return await AdminHumanListAssembler.AssembleAsync(
            allUsers,
            notificationEmails,
            searchUserIds,
            statusFilter,
            _membershipCalculator,
            ct);
    }

    // ─── Own Profile (Me) ────────────────────────────────────────────

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Me));

    [HttpGet("Me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var info = await GetCurrentUserInfoAsync(ct);
        if (info is null)
            return NotFound();

        var profile = info.Profile;
        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(info.Id, ct);
        var pendingConsentCount = snapshot.PendingConsentCount;

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(info.Id, ct);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

        var campaignGrants = await _campaignService.GetActiveOrCompletedGrantsForUserAsync(info.Id, ct);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = info.Id,
            HasPendingConsents = pendingConsentCount > 0,
            PendingConsentCount = pendingConsentCount,
            IsApproved = profile?.IsApproved ?? false,
            IsOwnProfile = true,
            DisplayName = info.DisplayName,
            CampaignGrants = campaignGrants,
        };

        // Show tier application status (skip Withdrawn — not interesting)
        if (latestApplication is not null && latestApplication.Status != ApplicationStatus.Withdrawn)
        {
            viewModel.TierApplicationStatus = latestApplication.Status;
            viewModel.TierApplicationTier = latestApplication.MembershipTier;
            viewModel.TierApplicationBadgeClass = latestApplication.Status.GetBadgeClass();
        }

        return View("Index", viewModel);
    }

    [HttpGet("Me/Edit")]
    public async Task<IActionResult> Edit([FromQuery] bool preview = false, CancellationToken ct = default)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info is null) return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id, ct);
        var allShiftTags = await _shiftMgmt.GetTagsAsync();
        var preferredShiftTags = await _shiftMgmt.GetVolunteerTagPreferencesAsync(user.Id);
        var externalLogins = await _userManager.GetLoginsAsync(user);

        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            allShiftTags,
            preferredShiftTags,
            preview,
            externalLogins.Any(l => string.Equals(l.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase)),
            p => Url.Action(nameof(Picture), new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

        ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        // Shift-tag catalog isn't posted back — repopulate up front so every
        // validation-failure `View(model)` path in this action still renders the picker.
        model.AllShiftTags = (await _shiftMgmt.GetTagsAsync())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        // Validate phone numbers start with + (E.164 format)
        var phoneTypes = new[] { ContactFieldType.Phone, ContactFieldType.WhatsApp };
        for (var i = 0; i < model.EditableContactFields.Count; i++)
        {
            var cf = model.EditableContactFields[i];
            if (!string.IsNullOrWhiteSpace(cf.Value) && phoneTypes.Contains(cf.FieldType) && !cf.Value.TrimStart().StartsWith("+", StringComparison.Ordinal))
            {
                ModelState.AddModelError($"EditableContactFields[{i}].Value",
                    _localizer["Validation_PhoneE164", _localizer["Profile_" + cf.FieldType].Value].Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(model.EmergencyContactPhone) && !model.EmergencyContactPhone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.EmergencyContactPhone),
                _localizer["Validation_PhoneE164", _localizer["Profile_EmergencyContactPhone"].Value].Value);
        }

        if (ModelState.ErrorCount > 0)
        {
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Validate Burner CV: must have entries OR check "no prior experience"
        var hasVolunteerHistory = model.EditableVolunteerHistory
            .Any(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue);
        if (!model.NoPriorBurnExperience && !hasVolunteerHistory)
        {
            ModelState.AddModelError(nameof(model.NoPriorBurnExperience),
                _localizer["Profile_BurnerCVRequired"].Value);
            // Need to check if initial setup for the view
            var existingProfile = (await _userService.GetUserInfoAsync(user.Id))?.Profile;
            model.IsInitialSetup = existingProfile is null || !existingProfile.IsApproved;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Validate tier-specific fields during initial setup
        var profileForSetupCheck = (await _userService.GetUserInfoAsync(user.Id))?.Profile;
        var isInitialSetup = profileForSetupCheck is null || !profileForSetupCheck.IsApproved;
        if (isInitialSetup)
        {
            if (model.SelectedTier != MembershipTier.Volunteer &&
                string.IsNullOrWhiteSpace(model.ApplicationMotivation))
            {
                ModelState.AddModelError(nameof(model.ApplicationMotivation),
                    _localizer["Profile_MotivationRequired"].Value);
                model.IsInitialSetup = true;
                model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                    && string.IsNullOrEmpty(model.LastName)
                    && string.IsNullOrEmpty(model.EmergencyContactName);
                ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                return View(model);
            }

            if (model.SelectedTier == MembershipTier.Asociado)
            {
                if (string.IsNullOrWhiteSpace(model.ApplicationSignificantContribution))
                {
                    ModelState.AddModelError(nameof(model.ApplicationSignificantContribution),
                        _localizer["Application_SignificantContributionRequired"].Value);
                }
                if (string.IsNullOrWhiteSpace(model.ApplicationRoleUnderstanding))
                {
                    ModelState.AddModelError(nameof(model.ApplicationRoleUnderstanding),
                        _localizer["Application_RoleUnderstandingRequired"].Value);
                }
                if (!ModelState.IsValid)
                {
                    model.IsInitialSetup = true;
                    model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                        && string.IsNullOrEmpty(model.LastName)
                        && string.IsNullOrEmpty(model.EmergencyContactName);
                    ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                    return View(model);
                }
            }
        }

        var pictureUpload = await TryReadProfilePictureUploadAsync(model);
        if (!pictureUpload.Success)
        {
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var saveRequest = new ProfileSaveRequest(
            BurnerName: model.BurnerName,
            FirstName: model.FirstName,
            LastName: model.LastName,
            City: model.City,
            CountryCode: model.CountryCode,
            Latitude: model.Latitude,
            Longitude: model.Longitude,
            PlaceId: model.PlaceId,
            Bio: model.Bio,
            Pronouns: model.Pronouns,
            ContributionInterests: model.ContributionInterests,
            BoardNotes: model.BoardNotes,
            BirthdayMonth: model.BirthdayMonth,
            BirthdayDay: model.BirthdayDay,
            EmergencyContactName: model.EmergencyContactName,
            EmergencyContactPhone: model.EmergencyContactPhone,
            EmergencyContactRelationship: model.EmergencyContactRelationship,
            NoPriorBurnExperience: model.NoPriorBurnExperience,
            ProfilePictureData: pictureUpload.Data,
            ProfilePictureContentType: pictureUpload.ContentType,
            RemoveProfilePicture: model.RemoveProfilePicture);

        var profileId = await _profileService.SaveProfileAsync(
            user.Id, model.BurnerName, saveRequest,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        // Peer-call the director threshold check. ProfileService deliberately
        // does not call into Onboarding directly — that was the inverted arrow.
        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(user.Id);

        // Initial-setup tier-application orchestration. Same form submission
        // as profile fields, by design — onboarding efficiency. The form
        // disables the radios when a Submitted/Approved Application already
        // exists (`isTierLocked` in the GET) so this branch only runs when
        // either no application exists yet or an existing draft is being
        // edited; ApplicationDecisionService.SubmitAsync also rejects with
        // AlreadyPending as a backstop. See issue
        // nobodies-collective/Humans#685.
        if (isInitialSetup && model.SelectedTier != MembershipTier.Volunteer)
        {
            var existingApps = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);
            var existingDraft = existingApps.FirstOrDefault(a =>
                a.Status == ApplicationStatus.Submitted);
            var hasApprovedApp = existingApps.Any(a =>
                a.Status == ApplicationStatus.Approved);

            if (existingDraft is not null)
            {
                await _applicationDecisionService.UpdateDraftApplicationAsync(
                    existingDraft.Id,
                    model.SelectedTier,
                    model.ApplicationMotivation!,
                    model.ApplicationAdditionalInfo,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationSignificantContribution : null,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationRoleUnderstanding : null);
            }
            else if (!hasApprovedApp)
            {
                await _applicationDecisionService.SubmitAsync(
                    user.Id, model.SelectedTier,
                    model.ApplicationMotivation!,
                    model.ApplicationAdditionalInfo,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationSignificantContribution : null,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationRoleUnderstanding : null,
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            }
        }

        // Cancel any pending deletion request when creating a profile.
        // Routes through IAccountDeletionService so the deletion-fields
        // write + UserInfo invalidation goes through the orchestrator,
        // not raw UserManager.UpdateAsync.
        if (isInitialSetup && user.IsDeletionPending)
        {
            await _accountDeletionService.CancelDeletionAsync(user.Id);
            _logger.LogInformation(
                "Cancelled pending deletion request for user {UserId} on profile creation",
                user.Id);
        }

        // Save contact fields
        var contactFieldDtos = model.EditableContactFields
            .Where(cf => !string.IsNullOrWhiteSpace(cf.Value))
            .Select((cf, index) => new ContactFieldEditDto(
                cf.Id,
                cf.FieldType,
                cf.CustomLabel,
                cf.Value,
                cf.Visibility,
                index
            ))
            .ToList();

        try
        {
            await _contactFieldService.SaveContactFieldsAsync(profileId, contactFieldDtos);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning(ex, "Failed to save contact fields for user {UserId} and profile {ProfileId}", user.Id, profileId);
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Save CV entries. Id round-trips for existing rows so the row keeps
        // its identity and CreatedAt; new rows post Guid.Empty and get a
        // fresh Id assigned on insert.
        var cvEntries = model.EditableVolunteerHistory
            .Where(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue)
            .Select(vh => new CVEntry(
                vh.Id ?? Guid.Empty,
                vh.ParsedDate!.Value,
                vh.EventName,
                vh.Description
            ))
            .ToList();

        await _profileService.SaveCVEntriesAsync(user.Id, cvEntries);

        // Save profile languages (remove-and-replace)
        var newLanguages = model.EditableLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
            .Select(l => new ProfileLanguage
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                LanguageCode = l.LanguageCode.Trim(),
                Proficiency = l.Proficiency
            })
            .ToList();

        await _profileService.SaveProfileLanguagesAsync(profileId, newLanguages);

        await _shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, model.EditableShiftTagIds);

        SetSuccess(_localizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(Me));
    }

    private async Task<(bool Success, byte[]? Data, string? ContentType)> TryReadProfilePictureUploadAsync(ProfileViewModel model)
    {
        if (model.ProfilePictureUpload is not { Length: > 0 } upload)
        {
            return (true, null, null);
        }

        if (upload.Length > MaxProfilePictureUploadBytes)
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                _localizer["Profile_PictureTooLarge"].Value);
            return (false, null, null);
        }

        var uploadContentType = upload.ContentType;
        if (!AllowedImageContentTypes.Contains(uploadContentType))
        {
            var ext = Path.GetExtension(upload.FileName);
            if (!string.IsNullOrEmpty(ext) && HeifExtensionToContentType.TryGetValue(ext, out var mapped))
            {
                uploadContentType = mapped;
            }
        }

        if (!AllowedImageContentTypes.Contains(uploadContentType))
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                _localizer["Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        using var uploadStream = new MemoryStream();
        await upload.CopyToAsync(uploadStream);
        var result = ResizeProfilePicture(uploadStream.ToArray(), uploadContentType);
        if (result is null)
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                _localizer["Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        return (true, result.Value.Data, result.Value.ContentType);
    }

    [HttpGet("Me/Emails")]
    public async Task<IActionResult> Emails()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(user);
        return View(viewModel);
    }

    [HttpPost("Me/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.NewEmail) || !ModelState.IsValid)
        {
            if (string.IsNullOrWhiteSpace(model.NewEmail))
                ModelState.AddModelError(nameof(model.NewEmail), _localizer["Profile_EnterEmail"].Value);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        try
        {
            var result = await _userEmailService.AddEmailAsync(user.Id, model.NewEmail);
            await SendAddedEmailVerificationAsync(user, model.NewEmail, result);
            SetAddedEmailFlash(model.NewEmail, result.IsConflict);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning(
                "Rejected email add for user {UserId} ({Email}): {Reason}",
                user.Id, model.NewEmail, ex.Message);
            ModelState.AddModelError(nameof(model.NewEmail), ex.Message);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        return RedirectToAction(nameof(Emails));
    }

    private async Task SendAddedEmailVerificationAsync(User user, string email, AddEmailResult result)
    {
        var trimmedEmail = email.Trim();
        var verificationUrl = Url.Action(
            nameof(VerifyEmail),
            "Profile",
            new { userId = user.Id, emailId = result.EmailId, token = HttpUtility.UrlEncode(result.Token) },
            Request.Scheme);

        await _emailService.SendEmailVerificationAsync(
            trimmedEmail,
            user.DisplayName,
            verificationUrl!,
            result.IsConflict,
            user.PreferredLanguage);

        _logger.LogInformation(
            "Sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, user.Id, result.IsConflict);
    }

    private void SetAddedEmailFlash(string email, bool isConflict)
    {
        if (isConflict)
        {
            SetInfo("This email is linked to another account. Verifying it will request an account merge. Check your inbox for the verification link.");
            return;
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture, _localizer["Profile_VerificationSent"].Value, email.Trim()));
    }

    [HttpGet("Me/Emails/Verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, Guid emailId, string token)
    {
        if (string.IsNullOrEmpty(token) || emailId == Guid.Empty)
        {
            return VerifyEmailError(_localizer["Profile_InvalidVerificationLink"].Value);
        }

        try
        {
            var decodedToken = HttpUtility.UrlDecode(token);
            var result = await _userEmailService.VerifyEmailAsync(userId, emailId, decodedToken);
            _cache.InvalidateNobodiesTeamEmails();

            return VerifyEmailSuccess(userId, result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("Email verification failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(_localizer["Profile_InvalidVerificationLink"].Value);
        }
        catch (ValidationException ex)
        {
            _logger.LogInformation("Email verification validation failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(ex.Message);
        }
    }

    private IActionResult VerifyEmailSuccess(Guid userId, VerifyEmailResult result)
    {
        if (result.MergeRequestCreated)
        {
            _logger.LogInformation(
                "User {UserId} verified email {Email} - merge request created",
                userId, result.Email);

            ViewData["Success"] = true;
            ViewData["Message"] = $"Email verified. A merge request has been submitted for admin review. The email {result.Email} will be added to your account once approved.";
            return View("VerifyEmailResult");
        }

        _logger.LogInformation(
            "User {UserId} verified email {Email}",
            userId, result.Email);

        ViewData["Success"] = true;
        ViewData["Message"] = string.Format(_localizer["Profile_EmailVerified"].Value, result.Email);
        return View("VerifyEmailResult");
    }
    private IActionResult VerifyEmailError(string message)
    {
        ViewData["Success"] = false;
        ViewData["Message"] = message;
        return View("VerifyEmailResult");
    }

    [HttpPost("Me/Emails/SetPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            await _userEmailService.SetPrimaryAsync(user.Id, emailId, ct);
            _cache.InvalidateNobodiesTeamEmails();
            // Self-path audit — symmetric with AdminSetPrimary. SetPrimaryAsync
            // does not take actorUserId, so audit at the controller.
            await _auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), user.Id,
                $"Set primary email row {emailId}",
                user.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(_localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to set primary email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEmailVisibility(Guid emailId, string? visibility)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var parsedVisibility = ParseEmailVisibility(visibility);

        try
        {
            await _userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
            await LogSelfEmailVisibilityChangedAsync(user.Id, emailId, parsedVisibility);
            SetSuccess(_localizer["Profile_EmailVisibilityUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to set email visibility for email {EmailId} and user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private static ContactFieldVisibility? ParseEmailVisibility(string? visibility) =>
        !string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private Task LogSelfEmailVisibilityChangedAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility) =>
        _auditLogService.LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
    [HttpPost("Me/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEmail(Guid emailId)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var deleted = await _userEmailService.DeleteEmailAsync(user.Id, emailId);
            if (deleted)
            {
                _cache.InvalidateNobodiesTeamEmails();
                await LogSelfEmailDeletedAsync(user.Id, emailId);
                SetSuccess(_localizer["Profile_EmailDeleted"].Value);
            }
            else
            {
                // Provider-attached rows must go through Unlink, not Delete.
                SetError(_localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to delete email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private Task LogSelfEmailDeletedAsync(Guid userId, Guid emailId) =>
        _auditLogService.LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), userId,
            $"Deleted email row {emailId}",
            userId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
    [HttpPost("Me/Emails/SetGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGoogle(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await _userEmailService.SetGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to set Google service email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailResult(bool ok)
    {
        if (ok)
        {
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["EmailGrid_GoogleServiceUpdated"].Value);
            return;
        }

        SetError(_localizer["EmailGrid_SetGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearGoogle(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await _userEmailService.ClearGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to clear Google flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailClearedResult(bool ok)
    {
        if (ok)
        {
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["EmailGrid_GoogleFlagCleared"].Value);
            return;
        }

        SetError(_localizer["EmailGrid_ClearGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await _userEmailService.ClearPrimaryAsync(user.Id, emailId, user.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to clear primary flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetPrimaryEmailClearedResult(bool ok)
    {
        if (ok)
        {
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["EmailGrid_PrimaryFlagCleared"].Value);
            return;
        }

        SetError(_localizer["EmailGrid_ClearPrimaryRejected"].Value);
    }

    [HttpPost("Me/Emails/Link/{provider}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Link(string provider, string? returnUrl = null)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Route the OAuth round-trip through AccountController.ExternalLoginCallback
        // so the link-while-signed-in branch (UserManager.AddLoginAsync +
        // ReconcileOAuthIdentityAsync) actually fires after the provider
        // returns. Redirecting straight back to /Profile/Me/Emails would skip
        // that branch and the linkage would never persist.
        var resolvedReturnUrl = returnUrl ?? Url.Action(nameof(Emails)) ?? "/Profile/Me/Emails";
        var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl = resolvedReturnUrl })
            ?? "/Account/ExternalLoginCallback";
        var props = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(props, provider);
    }

    [HttpPost("Me/Emails/Unlink/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await _authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await _userEmailService.UnlinkAsync(user.Id, id, user.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to unlink email {EmailId} for user {UserId}", id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetEmailUnlinkedResult(bool ok)
    {
        if (ok)
        {
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["EmailGrid_UnlinkSuccess"].Value);
            return;
        }

        SetError(_localizer["EmailGrid_UnlinkRejected"].Value);
    }
    // Admin grid actions — parameterized by {userId}, mirror the self-grid
    // against a target user. No AdminLink because OAuth linking requires the
    // target user to authenticate with the provider.

    [HttpGet("{id:guid}/Admin/Emails")]
    public async Task<IActionResult> AdminEmails(Guid id, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var targetUser = await _userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(targetUser, isAdminContext: true, ct);
        return View("Emails", viewModel);
    }

    [HttpPost("{id:guid}/Admin/Emails/SetGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await _userEmailService.SetGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to set Google service email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/SetPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            await _userEmailService.SetPrimaryAsync(id, emailId, ct);
            _cache.InvalidateNobodiesTeamEmails();
            // Audit at the controller — SetPrimaryAsync does not take actorUserId.
            await _auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), id,
                $"Admin set primary email row {emailId}",
                actor.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(_localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to set primary email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await _userEmailService.ClearGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to clear Google flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await _userEmailService.ClearPrimaryAsync(id, emailId, actor.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to clear primary flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddEmail(Guid id, string email, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(_localizer["Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var targetUser = await _userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        try
        {
            var result = await _userEmailService.AddEmailAsync(id, email, ct);
            await SendAdminAddedEmailVerificationAsync(id, targetUser, email, result, actor.Id, ct);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Admin failed to add email for user {UserId} ({Email}): {Reason}",
                id, email, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SendAdminAddedEmailVerificationAsync(
        Guid userId,
        User targetUser,
        string email,
        AddEmailResult result,
        Guid actorId,
        CancellationToken ct)
    {
        var trimmedEmail = email.Trim();
        var verificationUrl = Url.Action(
            nameof(VerifyEmail),
            "Profile",
            new { userId, emailId = result.EmailId, token = HttpUtility.UrlEncode(result.Token) },
            Request.Scheme);

        await _emailService.SendEmailVerificationAsync(
            trimmedEmail,
            targetUser.DisplayName,
            verificationUrl!,
            result.IsConflict,
            targetUser.PreferredLanguage,
            ct);

        _logger.LogInformation(
            "Admin sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, userId, result.IsConflict);

        await _auditLogService.LogAsync(
            AuditAction.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pending email {trimmedEmail} for user {userId} (conflict: {result.IsConflict})",
            actorId);

        SetSuccess(_localizer["EmailGrid_AdminAddSentVerification"].Value);
    }
    // Admin-only recovery path: directly insert an already-verified UserEmail
    // row without sending a verification email. Use when an admin needs to
    // restore a row that was deleted in error (or otherwise re-attach a known
    // address to the user without a round-trip to their mailbox).
    [HttpPost("{id:guid}/Admin/Emails/AddVerified")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddVerifiedEmail(Guid id, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(_localizer["Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await _userManager.GetUserAsync(User);
        var targetUser = await _userManager.FindByIdAsync(id.ToString());

        return await AdminAddVerifiedEmailAsync(id, email.Trim(), actor, targetUser, ct);
    }

    private async Task<IActionResult> AdminAddVerifiedEmailAsync(
        Guid userId,
        string email,
        User? actor,
        User? targetUser,
        CancellationToken ct)
    {
        if (actor is null)
            return Forbid();

        if (targetUser is null)
            return NotFound();

        try
        {
            var inserted = await _userEmailService.AddVerifiedEmailAsync(userId, email, ct);
            await ReportVerifiedEmailAddAsync(inserted, userId, email, actor.Id);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Admin failed to add verified email for user {UserId} ({Email}): {Reason}",
                userId, email, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id = userId });
    }

    private async Task ReportVerifiedEmailAddAsync(
        bool inserted,
        Guid userId,
        string email,
        Guid actorUserId)
    {
        if (!inserted)
        {
            SetInfo($"Email {email} already exists on this user — no change.");
            return;
        }

        _cache.InvalidateNobodiesTeamEmails();

        await _auditLogService.LogAsync(
            AuditAction.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pre-verified email {email} for user {userId} (no verification flow)",
            actorUserId);

        SetSuccess($"Verified email {email} added.");
    }

    [HttpPost("{id:guid}/Admin/Emails/Verify")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminVerifyEmail(Guid id, Guid emailId, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var result = await _userEmailService.AdminMarkVerifiedAsync(id, emailId, actor.Id, ct);
            _cache.InvalidateNobodiesTeamEmails();
            if (result.MergeRequestCreated)
            {
                SetSuccess(_localizer["EmailGrid_AdminVerifyMergeRequested"].Value);
            }
            else
            {
                SetSuccess(_localizer["EmailGrid_AdminVerifySuccess"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Admin failed to manually verify email {EmailId} for user {UserId}: {Reason}",
                emailId, id, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Unlink/{emailId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminUnlink(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await _userEmailService.UnlinkAsync(id, emailId, actor.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to unlink email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminDeleteEmail(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var deleted = await _userEmailService.DeleteEmailAsync(id, emailId, ct);
            await SetAdminEmailDeletedResultAsync(id, emailId, actor.Id, deleted);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to delete email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailDeletedResultAsync(Guid userId, Guid emailId, Guid actorId, bool deleted)
    {
        if (!deleted)
        {
            SetError(_localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            return;
        }

        _cache.InvalidateNobodiesTeamEmails();
        await _auditLogService.LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), userId,
            $"Admin deleted email row {emailId}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(_localizer["Profile_EmailDeleted"].Value);
    }
    [HttpPost("{id:guid}/Admin/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetVisibility(
        Guid id, Guid emailId, ContactFieldVisibility? visibility, CancellationToken ct)
    {
        var authz = await _authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            await _userEmailService.SetVisibilityAsync(id, emailId, visibility, ct);
            await SetAdminEmailVisibilityChangedResultAsync(id, emailId, actor.Id, visibility);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Admin failed to set email visibility for email {EmailId} and user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailVisibilityChangedResultAsync(
        Guid userId,
        Guid emailId,
        Guid actorId,
        ContactFieldVisibility? visibility)
    {
        await _auditLogService.LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Admin changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(_localizer["Profile_EmailVisibilityUpdated"].Value);
    }
    [HttpGet("Me/Outbox")]
    public async Task<IActionResult> MyOutbox()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var messages = await _emailOutboxService.GetMessagesForUserAsync(user.Id);

        return View("Outbox", messages);
    }

    [HttpGet("Me/Privacy")]
    public async Task<IActionResult> Privacy()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var viewModel = new PrivacyViewModel
        {
            IsDeletionPending = user.IsDeletionPending,
            DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc(),
            DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc()
        };

        ViewData["DpoEmail"] = _configuration.GetOptionalSetting(_configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
        return View(viewModel);
    }

    [HttpPost("Me/Privacy/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await _accountDeletionService.RequestDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                SetError(_localizer["Profile_DeletionAlreadyPending"].Value);
            return RedirectToAction(nameof(Privacy));
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture,
            _localizer["Profile_DeletionRequested"].Value,
            result.EffectiveDeletionDate?.ToDateTimeUtc().ToDisplayLongDate() ?? ""));
        return RedirectToAction(nameof(Privacy));
    }

    [HttpPost("Me/Privacy/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await _accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal))
                SetError(_localizer["Profile_NoDeletionPending"].Value);
            return RedirectToAction(nameof(Privacy));
        }

        SetSuccess(_localizer["Profile_DeletionCancelled"].Value);
        return RedirectToAction(nameof(Privacy));
    }

    [HttpGet("Me/ShiftInfo")]
    public async Task<IActionResult> ShiftInfo()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound(); var profile = await _shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);
            return View(ShiftInfoViewModel.FromProfile(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load shift info for user");
            SetError("Failed to load shift info.");
            return RedirectToAction(nameof(Me));
        }
    }

    [HttpPost("Me/ShiftInfo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShiftInfo(ShiftInfoViewModel model)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            var shiftProfile = await _shiftMgmt.GetOrCreateShiftProfileAsync(user.Id);

            shiftProfile.Skills = ShiftInfoViewModel.MergeSkills(
                model.SelectedSkills, model.SkillOtherText, shiftProfile.Skills);
            shiftProfile.Quirks = ShiftInfoViewModel.MergePersistedQuirks(
                model.TimePreference, model.SelectedQuirks, shiftProfile.Quirks);
            shiftProfile.Languages = ShiftInfoViewModel.MergeLanguages(
                model.SelectedLanguages, model.LanguageOtherText, shiftProfile.Languages);

            await _shiftMgmt.UpdateShiftProfileAsync(shiftProfile);

            SetSuccess(_localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(ShiftInfo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save shift info for user");
            SetError("Failed to save shift info.");
            return View(model);
        }
    }

    [HttpGet("Me/CommunicationPreferences")]
    public async Task<IActionResult> CommunicationPreferences()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            // Panel rendering moved to CommunicationPreferencesPanelViewComponent (issue #706).
            // The view invokes the VC with the current user's id; no model needed.
            return View(model: user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load communication preferences");
            SetError("Failed to load communication preferences.");
            return RedirectToAction(nameof(Me));
        }
    }

    [HttpPost("Me/CommunicationPreferences/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, bool emailEnabled, bool alertEnabled)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return Unauthorized();

            if (category.IsAlwaysOn())
                return BadRequest("Cannot change always-on categories.");

            await _commPrefService.UpdatePreferenceAsync(
                user.Id, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Profile");

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }

    [HttpGet("Me/Notifications")]
    public IActionResult Notifications() => RedirectToActionPermanent(nameof(CommunicationPreferences));

    [HttpGet("Me/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var export = await _gdprExportService.ExportForUserAsync(user.Id, ct);

        var payload = BuildExportPayload(export);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, ExportJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"nobodies-profiles-export-{_clock.GetCurrentInstant().ToDateTimeUtc().ToIsoDateString()}.json";

        return File(bytes, "application/json", fileName);
    }

    private static Dictionary<string, object?> BuildExportPayload(GdprExport export)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExportedAt"] = export.ExportedAt
        };
        foreach (var (section, data) in export.Sections)
        {
            payload[section] = data;
        }
        return payload;
    }

    // ─── Shared (Profile Picture) ────────────────────────────────────

    [HttpGet("Picture")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id, CancellationToken ct)
    {
        // Per design-rules §2 the controller does not talk to the picture
        // store directly — IProfileService owns the FS-first / DB-fallback /
        // migrate-on-read orchestration AND the anonymization gate (issue
        // nobodies-collective/Humans#527).
        var result = await _profileService.GetProfilePictureAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        return File(result.Value.Data, result.Value.ContentType);
    }

    [HttpPost("Me/ImportGooglePhoto")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportGooglePhoto(CancellationToken ct)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        var externalLogins = await _userManager.GetLoginsAsync(user);
        if (!HasGoogleAvatarSource(user, externalLogins))
        {
            SetError(_localizer["Profile_ImportGooglePhoto_Unavailable"].Value);
            return RedirectToAction(nameof(Edit));
        }

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info?.Profile is null)
        {
            SetError(_localizer["Profile_ImportGooglePhoto_NoProfile"].Value);
            return RedirectToAction(nameof(Edit));
        }
        if (info.Profile.HasCustomPicture)
        {
            SetError(_localizer["Profile_ImportGooglePhoto_AlreadyHasCustom"].Value);
            return RedirectToAction(nameof(Edit));
        }

        if (!TryGetTrustedGoogleAvatarUri(user, out var pictureUri))
        {
            SetError(_localizer["Profile_ImportGooglePhoto_NotGoogleUrl"].Value);
            return RedirectToAction(nameof(Edit));
        }

        var rawBytes = await FetchGoogleAvatarBytesAsync(pictureUri, user.Id, ct);
        if (rawBytes is null)
        {
            return RedirectToAction(nameof(Edit));
        }

        var resized = Helpers.ProfilePictureProcessor.ResizeProfilePicture(rawBytes, _logger);
        if (resized is null)
        {
            SetError(_localizer["Profile_ImportGooglePhoto_InvalidFormat"].Value);
            return RedirectToAction(nameof(Edit));
        }

        await _profileService.SetProfilePictureAsync(user.Id, resized.Value.Data, resized.Value.ContentType, ct);

        _logger.LogInformation("Imported Google avatar for user {UserId}", user.Id);
        SetSuccess(_localizer["Profile_ImportGooglePhoto_Success"].Value);
        return RedirectToAction(nameof(Edit));
    }


    private static bool HasGoogleAvatarSource(User user, IEnumerable<UserLoginInfo> externalLogins) =>
        !string.IsNullOrEmpty(user.ProfilePictureUrl)
        && externalLogins.Any(l => string.Equals(l.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase));

    private bool TryGetTrustedGoogleAvatarUri(User user, out Uri pictureUri)
    {
        if (Uri.TryCreate(user.ProfilePictureUrl, UriKind.Absolute, out pictureUri!)
            && string.Equals(pictureUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && pictureUri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.LogWarning(
            "Refusing to import Google photo for user {UserId}: URL is not a trusted Google host",
            user.Id);
        return false;
    }

    private async Task<byte[]?> FetchGoogleAvatarBytesAsync(Uri pictureUri, Guid userId, CancellationToken ct)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(GoogleAvatarHttpClientName);
            using var response = await httpClient.GetAsync(pictureUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google avatar fetch for user {UserId} returned {StatusCode}",
                    userId, (int)response.StatusCode);
                SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return null;
            }

            var fetchedContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AllowedImageContentTypes.Contains(fetchedContentType))
            {
                _logger.LogWarning(
                    "Google avatar for user {UserId} returned unsupported content type {ContentType}",
                    userId, fetchedContentType);
                SetError(_localizer["Profile_ImportGooglePhoto_InvalidFormat"].Value);
                return null;
            }

            var rawBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (rawBytes.Length == 0 || rawBytes.Length > MaxGooglePhotoDownloadBytes)
            {
                _logger.LogWarning(
                    "Google avatar for user {UserId} had invalid size {Bytes}", userId, rawBytes.Length);
                SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return null;
            }

            return rawBytes;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google avatar fetch failed for user {UserId}", userId);
            SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Google avatar fetch timed out for user {UserId}", userId);
            SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
            return null;
        }
    }
    // ─── View Another Profile ────────────────────────────────────────

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ViewProfile(Guid id, CancellationToken ct)
    {
        var profileInfo = await _userService.GetUserInfoAsync(id, ct);
        var profile = profileInfo?.Profile;

        if (profile is null || profileInfo!.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await GetCurrentUserInfoAsync(ct);
        if (viewer is null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;

        var noShowContext = await BuildNoShowHistoryContextAsync(id, viewer.Id, isOwnProfile, ct);

        // The ProfileCard ViewComponent handles all data fetching and permission checks.
        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profileInfo!.DisplayName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowContext.History,
            CanViewShiftSignups = noShowContext.CanView,
        };

        return View("Index", viewModel);
    }

    private async Task<(bool CanView, List<NoShowHistoryItem>? History)> BuildNoShowHistoryContextAsync(
        Guid profileUserId,
        Guid viewerId,
        bool isOwnProfile,
        CancellationToken ct)
    {
        if (isOwnProfile)
        {
            return (false, null);
        }

        var viewerIsCoordinator = (await _shiftMgmt.GetCoordinatorTeamIdsAsync(viewerId)).Count > 0;
        var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        if (!viewerCanViewShiftHistory)
        {
            return (false, null);
        }

        var noShows = await _shiftSignupService.GetNoShowHistoryAsync(profileUserId);
        if (noShows.Count == 0)
        {
            return (true, null);
        }

        var noShowTeamIds = noShows.Select(s => s.TeamId).Distinct().ToList();
        var teamsById = await _teamService.GetTeamsAsync(ct);
        var noShowTeamNames = noShowTeamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        var reviewerIds = noShows
            .Where(s => s.ReviewedByUserId.HasValue)
            .Select(s => s.ReviewedByUserId!.Value)
            .Distinct()
            .ToList();
        var reviewers = reviewerIds.Count == 0
            ? (IReadOnlyDictionary<Guid, UserInfo>)new Dictionary<Guid, UserInfo>()
            : await _userService.GetUserInfosAsync(reviewerIds, ct);

        return (true, noShows.Select(s =>
        {
            var signupTz = DateTimeZoneProviders.Tzdb[s.TimeZoneId];
            var zoned = s.ShiftStart.InZone(signupTz);
            var reviewer = s.ReviewedByUserId.HasValue
                ? reviewers.GetValueOrDefault(s.ReviewedByUserId.Value)
                : null;
            return new NoShowHistoryItem
            {
                ShiftLabel = s.ShiftLabel,
                DepartmentName = noShowTeamNames.GetValueOrDefault(s.TeamId, ""),
                ShiftDateLabel = zoned.ToDisplayShortDateTime(),
                MarkedByName = reviewer?.DisplayName,
                MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDisplayShortMonthDayTime()
            };
        }).ToList());
    }

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null) return NotFound();

        var profile = info.Profile;
        if (profile is null)
        {
            return PartialView("_HumanPopover",
                ProfileSummaryViewModelBuilder.BuildWithoutProfile(info));
        }

        var memberships = await _teamService.GetActiveTeamMembershipsForUserAsync(id, ct);
        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(
            info,
            memberships,
            p => Url.Action(nameof(Picture), "Profile",
                new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

        return PartialView("_HumanPopover", vm);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        var targetUser = await _userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        if (!await _commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetUser.DisplayName
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/SendMessage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageViewModel model)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        // Issue #635 (§15i): bulk-fetch sender + recipient with UserEmails
        // hydrated through the section-owned service instead of a raw
        // `.Include(u => u.UserEmails)` over the cross-domain nav.
        var participants = await _userService.GetUserInfosAsync([id, currentUser.Id]);
        if (!participants.TryGetValue(id, out var targetUser))
            return NotFound();

        if (!await _commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.DisplayName;

        if (!ModelState.IsValid)
            return View(model);

        if (!participants.TryGetValue(currentUser.Id, out var sender))
            return NotFound();

        var request = FacilitatedMessageRequestBuilder.TryBuild(sender, targetUser, model);
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }

        await _emailService.SendFacilitatedMessageAsync(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.CleanMessage,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage);

        await _auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            nameof(User), targetUser.Id,
            $"Message sent to {targetUser.DisplayName} (contact info shared: {(model.IncludeContactInfo ? "yes" : "no")})",
            currentUser.Id);

        SetSuccess(string.Format(
            _localizer["SendMessage_Success"].Value,
            targetUser.DisplayName));

        return RedirectToAction(nameof(ViewProfile), new { id });
    }

    // ─── Search ──────────────────────────────────────────────────────

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var viewModel = new HumanSearchViewModel { Query = q };

        if (!q.HasSearchTerm())
        {
            return View(viewModel);
        }

        // PublicAll = name + bio + public ContactFields. Admin bit is gated
        // by code review — never set on a public endpoint.
        var results = await _userService.SearchUsersAsync(
            q!, PersonSearchFields.PublicAll, limit: 50, ct);

        // Display ordering at the controller per
        // memory/architecture/display-sort-in-controllers.md.
        viewModel.Results = results
            .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.ToHumanSearchViewModel())
            .ToList();

        return View(viewModel);
    }

    // ─── Admin: All Humans List ──────────────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("Admin")]
    public async Task<IActionResult> AdminList(string? search, string? filter, string sort = "name", string dir = "asc", int page = 1, CancellationToken ct = default)
    {
        var allRows = await BuildAdminHumansAsync(search, filter, ct);
        var viewModel = AdminHumanListViewModelBuilder.Build(
            allRows,
            search,
            filter,
            sort,
            dir,
            page,
            id => Url.Action(nameof(AdminDetail), "Profile", new { id }));

        return View("AdminList", viewModel);
    }

    // ─── Admin: Per-Person Detail ────────────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin")]
    public async Task<IActionResult> AdminDetail(Guid id, CancellationToken ct)
    {
        // Per-section composition (issue nobodies-collective/Humans#685): the
        // Profile section reads its own row; cross-section data (Applications,
        // RoleAssignments, ConsentCount, UserEmails, rejected-by display name)
        // is fetched from each owning section here.
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null)
            return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(id, ct);
        var userEmails = await _userEmailService.GetEntitiesByUserIdAsync(id, ct);
        var consentCount = await _consentService.GetConsentRecordCountAsync(id, ct);
        var roleAssignments = await _roleAssignmentService.GetByUserIdAsync(id, ct);
        var roleCreatorNamesByUserId = (await _userService.GetByIdsAsync(
                roleAssignments.Select(ra => ra.CreatedByUserId).Distinct().ToList(), ct))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.DisplayName);
        var campaignGrants = await _campaignService.GetAllGrantsForUserAsync(id, ct);
        var outboxCount = await _emailOutboxService.GetMessageCountForUserAsync(id, ct);
        var revealedIban = TempData.TryGetValue("RevealedIban", out var revealed) && revealed is string value
            ? value
            : null;

        var viewModel = AdminHumanDetailViewModelBuilder.Build(
            info,
            applications,
            userEmails,
            consentCount,
            roleAssignments,
            roleCreatorNamesByUserId,
            campaignGrants,
            outboxCount,
            _clock.GetCurrentInstant(),
            await GetRejectedByNameAsync(info.Profile, ct),
            revealedIban);

        return View("AdminDetail", viewModel);
    }

    private async Task<string?> GetRejectedByNameAsync(ProfileInfo? profile, CancellationToken ct)
    {
        if (profile?.RejectedByUserId is null)
            return null;

        var rejectedByInfo = await _userService.GetUserInfoAsync(profile.RejectedByUserId.Value, ct);
        return rejectedByInfo?.DisplayName;
    }

    /// <summary>
    /// Reveals the unmasked IBAN for one page load (TempData), and writes an audit entry.
    /// Admin-only: only users in the Admin role may reveal raw IBANs on the admin user page.
    /// </summary>
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpPost("{id:guid}/Admin/RevealIban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealIban(Guid id, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Forbid();
        return await RevealIbanCoreAsync(id, actor.Id, ct);
    }

    private async Task<IActionResult> RevealIbanCoreAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        var iban = info?.Profile?.Iban;
        if (iban is null)
        {
            SetError("No IBAN on record for this user.");
            return RedirectToAction(nameof(AdminDetail), new { id });
        }
        await _auditLogService.LogAsync(
            AuditAction.IbanReveal, "User", id,
            $"Admin revealed IBAN for user {id}", actorId);
        TempData["RevealedIban"] = iban;
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id, CancellationToken ct)
    {
        var messages = await _emailOutboxService.GetMessagesForUserAsync(id, ct);

        ViewBag.HumanId = id;
        return View("Outbox", messages);
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _humanLifecycleService.SuspendAsync(id, currentUser.Id, notes);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberSuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _humanLifecycleService.UnsuspendAsync(id, currentUser.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberUnsuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveVolunteer(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.ApproveVolunteerAsync(id, currentUser.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_VolunteerApproved"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return Unauthorized();

        var result = await _onboardingService.RejectSignupAsync(id, currentUser.Id, reason);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal))
                SetError("This human has already been rejected.");
            else
                return NotFound();
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        SetSuccess("Signup rejected.");
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/Roles/Add")]
    public async Task<IActionResult> AddRole(Guid id)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return NotFound();
        }

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            UserDisplayName = user.DisplayName,
            AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)]
        };

        return View(viewModel);
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            PopulateRoleAssignmentForm(model, id, user.DisplayName);
            return View(model);
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await _authorizationService.AuthorizeAsync(
            User, model.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            _logger.LogWarning(
                "Authorization denied for role assignment: principal {Principal} attempted to assign role {Role} to user {UserId}",
                User.Identity?.Name, model.RoleName, id);
            return Forbid();
        }

        var result = await _roleAssignmentService.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, model.Notes);

        SetRoleAssignmentResult(model.RoleName, result.Success);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    private void PopulateRoleAssignmentForm(CreateRoleAssignmentViewModel model, Guid userId, string userDisplayName)
    {
        model.UserId = userId;
        model.UserDisplayName = userDisplayName;
        model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
    }

    private void SetRoleAssignmentResult(string roleName, bool success)
    {
        if (success)
        {
            SetSuccess(string.Format(_localizer["Admin_RoleAssigned"].Value, roleName));
            return;
        }

        SetError(string.Format(_localizer["Admin_RoleAlreadyActive"].Value, roleName));
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await _roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment is null)
        {
            // Return NotFound rather than Unauthorized to prevent role-assignment enumeration.
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await _authorizationService.AuthorizeAsync(
            User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            _logger.LogWarning(
                "Authorization denied for ending role: principal {Principal} attempted to end role {Role} for user {UserId}",
                User.Identity?.Name, roleAssignment.RoleName, roleAssignment.UserId);
            return NotFound();
        }

        var result = await _roleAssignmentService.EndRoleAsync(
            roleId, currentUser.Id, notes);

        SetRoleEndedResult(result.Success, roleAssignment.RoleName, roleAssignment.UserDisplayName);
        return RedirectToAction(nameof(AdminDetail), new { id = roleAssignment.UserId });
    }


    private void SetRoleEndedResult(bool success, string roleName, string userDisplayName)
    {
        if (success)
        {
            SetSuccess(string.Format(_localizer["Admin_RoleEnded"].Value, roleName, userDisplayName));
            return;
        }

        SetError(_localizer["Admin_RoleNotActive"].Value);
    }
    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData, string contentType) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, _logger);

    private async Task<EmailsViewModel> BuildEmailsViewModelAsync(User user, bool isAdminContext = false, CancellationToken ct = default)
    {
        var emails = await _userEmailService.GetUserEmailsAsync(user.Id, ct);

        var canAdd = true;
        var minutesUntilResend = 0;

        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail is not null)
        {
            var (cooldownCanAdd, cooldownMinutes, _) =
                await _profileService.GetEmailCooldownInfoAsync(pendingEmail.Id, ct);
            canAdd = cooldownCanAdd;
            minutesUntilResend = cooldownMinutes;
        }

        var hasNobodiesTeam = emails.Any(e => e.IsVerified &&
            e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        // Issue nobodies-collective/Humans#687: TryBackfillGoogleEmailAsync
        // call removed. UserEmail.IsGoogle is sole source of truth and is
        // maintained by UserEmailService.EnsureGoogleInvariantAsync on every
        // row creation / verification; the legacy User.GoogleEmail shadow
        // column is no longer read, so the backfill is dead.

        // Use the already-loaded `emails` list (from GetUserEmailsAsync above) rather
        // than user.UserEmails — UserManager.GetUserAsync / FindByIdAsync don't
        // .Include(UserEmails), so the navigation would lazily reload (or be empty).
        var googleServiceEmail = emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();

        // Workspace canonical identity: Provider=Google AND email on the configured
        // Workspace domain. While present, Primary + Google radios lock to that row.
        // If multiple match (shouldn't happen), prefer IsPrimary, else first.
        var workspaceDomainSuffix = "@" + _googleWorkspaceOptions.Domain;
        var workspaceCandidates = emails
            .Where(e => !string.IsNullOrEmpty(e.Provider)
                && string.Equals(e.Provider, "Google", StringComparison.OrdinalIgnoreCase)
                && e.Email.EndsWith(workspaceDomainSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var workspaceLockedEmail = workspaceCandidates.FirstOrDefault(e => e.IsPrimary)
            ?? workspaceCandidates.FirstOrDefault();

        // Issue nobodies-collective/Humans#697: per-user admin diagnostic —
        // load AspNetUserLogins alongside UserEmail rows and compute the
        // store-disagreement flags. Self contexts skip the lookup (the section
        // is admin-only).
        IReadOnlyList<(string Provider, string ProviderKey)> userLogins =
            [];
        IReadOnlyList<UserEmailRowSnapshot> rawUserEmails =
            [];
        if (isAdminContext)
        {
            var loginsByUser = await _userService.GetExternalLoginsByUserIdsAsync([user.Id], ct);
            if (loginsByUser.TryGetValue(user.Id, out var list))
                userLogins = list;
            rawUserEmails = await _userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        }

        bool RowHasOrphanProviderTag(string? provider, string? providerKey) =>
            isAdminContext
            && !string.IsNullOrEmpty(provider)
            && !string.IsNullOrEmpty(providerKey)
            && !userLogins.Any(l =>
                string.Equals(l.Provider, provider, StringComparison.Ordinal)
                && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));

        bool LoginHasOrphanRow(string provider, string providerKey) =>
            !emails.Any(e =>
                string.Equals(e.Provider, provider, StringComparison.Ordinal)
                && string.Equals(e.ProviderKey, providerKey, StringComparison.Ordinal));

        static string HashForDisplay(string s)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(bytes.AsSpan(0, 8));
        }

        return new EmailsViewModel
        {
            Emails = emails.Select(e => new EmailRowViewModel
            {
                Id = e.Id,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsGoogle = e.IsGoogle,
                IsPrimary = e.IsPrimary,
                Visibility = e.Visibility,
                IsPendingVerification = e.IsPendingVerification,
                IsMergePending = e.IsMergePending,
                IsNobodiesTeamDomain = e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase),
                Provider = e.Provider,
                HasOrphanProviderTag = RowHasOrphanProviderTag(e.Provider, e.ProviderKey),
            }).ToList(),
            ExternalLogins = userLogins.Select(l => new ExternalLoginRowViewModel
            {
                LoginProvider = l.Provider,
                ProviderKeyHash = HashForDisplay(l.ProviderKey),
                ProviderDisplayName = null,
                HasOrphanLogin = LoginHasOrphanRow(l.Provider, l.ProviderKey),
            }).ToList(),
            RawUserEmails = rawUserEmails,
            CanAddEmail = canAdd,
            MinutesUntilResend = minutesUntilResend,
            GoogleServiceEmail = googleServiceEmail,
            HasNobodiesTeamEmail = hasNobodiesTeam,
            GoogleEmailStatus = user.GoogleEmailStatus,
            TargetUserId = user.Id,
            TargetDisplayName = user.DisplayName,
            IsAdminContext = isAdminContext,
            WorkspaceLockedEmailId = workspaceLockedEmail?.Id,
            LegacyIdentityEmailColumn = isAdminContext
                && User.IsInRole(Domain.Constants.RoleNames.Admin)
                ? user.IdentityEmailColumn
                : null,
        };
    }

}







