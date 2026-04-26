// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Humans.Application.Authorization;
using Humans.Application.Configuration;
using Humans.Application.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Gdpr;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

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
    private readonly IHttpClientFactory _httpClientFactory;

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
        UserManager<User> userManager,
        IProfileService profileService,
        IContactFieldService contactFieldService,
        IEmailService emailService,
        IUserEmailService userEmailService,
        ICommunicationPreferenceService commPrefService,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
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
        IUserService userService,
        IHttpClientFactory httpClientFactory)
        : base(userManager)
    {
        _userManager = userManager;
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _emailService = emailService;
        _userEmailService = userEmailService;
        _commPrefService = commPrefService;
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
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
        _httpClientFactory = httpClientFactory;
    }

    // ─── Own Profile (Me) ────────────────────────────────────────────

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Me));

    [HttpGet("Me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var (profile, latestApplication, pendingConsentCount) =
            await _profileService.GetProfileIndexDataAsync(user.Id, ct);
        var campaignGrants = await _profileService.GetActiveOrCompletedCampaignGrantsAsync(user.Id, ct);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = user.Id,
            HasPendingConsents = pendingConsentCount > 0,
            PendingConsentCount = pendingConsentCount,
            IsApproved = profile?.IsApproved ?? false,
            IsOwnProfile = true,
            DisplayName = user.DisplayName,
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
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var (profile, isTierLocked, pendingApplication) =
            await _profileService.GetProfileEditDataAsync(user.Id, ct);

        // Get all contact fields for editing
        var contactFields = profile is not null
            ? await _contactFieldService.GetAllContactFieldsAsync(profile.Id, ct)
            : [];

        // Get CV entries for editing from the FullProfile projection
        var fullProfile = await _profileService.GetFullProfileAsync(user.Id, ct);
        var cvEntries = fullProfile?.CVEntries ?? [];

        // Get profile languages for editing
        var languages = profile is not null
            ? await _profileService.GetProfileLanguagesAsync(profile.Id, ct)
            : (IReadOnlyList<ProfileLanguage>)[];

        var hasCustomPicture = profile?.HasCustomProfilePicture == true;

        // Initial setup = no profile or not yet approved (onboarding)
        // ?preview=true forces initial-setup mode for testing
        var isInitialSetup = profile is null || !profile.IsApproved || preview;

        // "Import my Google photo" button is only offered when the user signed in with
        // Google, we captured an avatar URL at sign-in, and they don't yet have a custom
        // upload. We intentionally don't surface a replace flow here (see issue #532).
        var externalLogins = await UserManager.GetLoginsAsync(user);
        var hasGoogleLogin = externalLogins.Any(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase));
        var canImportGooglePicture = hasGoogleLogin
            && !hasCustomPicture
            && !string.IsNullOrEmpty(user.ProfilePictureUrl);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture
                ? Url.Action(nameof(Picture), new { id = profile!.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
                : null,
            CanImportGooglePicture = canImportGooglePicture,
            BurnerName = profile?.BurnerName ?? user.DisplayName,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Latitude = profile?.Latitude,
            Longitude = profile?.Longitude,
            PlaceId = profile?.PlaceId,
            Bio = profile?.Bio,
            Pronouns = profile?.Pronouns,
            ContributionInterests = profile?.ContributionInterests,
            BoardNotes = profile?.BoardNotes,
            BirthdayMonth = profile?.DateOfBirth?.Month,
            BirthdayDay = profile?.DateOfBirth?.Day,
            EmergencyContactName = profile?.EmergencyContactName,
            EmergencyContactPhone = profile?.EmergencyContactPhone,
            EmergencyContactRelationship = profile?.EmergencyContactRelationship,
            CanViewLegalName = true, // User editing their own profile
            IsInitialSetup = isInitialSetup,
            SelectedTier = profile?.MembershipTier ?? MembershipTier.Volunteer,
            IsTierLocked = isTierLocked,
            ApplicationMotivation = pendingApplication?.Motivation,
            ApplicationAdditionalInfo = pendingApplication?.AdditionalInfo,
            ApplicationSignificantContribution = pendingApplication?.SignificantContribution,
            ApplicationRoleUnderstanding = pendingApplication?.RoleUnderstanding,
            NoPriorBurnExperience = profile?.NoPriorBurnExperience ?? false,
            ShowPrivateFirst = string.IsNullOrEmpty(profile?.FirstName)
                && string.IsNullOrEmpty(profile?.LastName)
                && string.IsNullOrEmpty(profile?.EmergencyContactName),
            EditableContactFields = contactFields.Select(cf => new ContactFieldEditViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                CustomLabel = cf.CustomLabel,
                Value = cf.Value,
                Visibility = cf.Visibility,
                DisplayOrder = cf.DisplayOrder
            }).ToList(),
            EditableVolunteerHistory = cvEntries.Select(cv => new VolunteerHistoryEntryEditViewModel
            {
                Id = cv.Id,
                DateString = cv.Date.ToIsoDateString(),
                EventName = cv.EventName,
                Description = cv.Description
            }).ToList(),
            EditableLanguages = languages.Select(pl => new ProfileLanguageEditViewModel
            {
                Id = pl.Id,
                LanguageCode = pl.LanguageCode,
                Proficiency = pl.Proficiency
            }).ToList()
        };

        ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var user = await GetCurrentUserAsync();
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
            var existingProfile = await _profileService.GetProfileAsync(user.Id);
            model.IsInitialSetup = existingProfile is null || !existingProfile.IsApproved;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Validate tier-specific fields during initial setup
        var profileForSetupCheck = await _profileService.GetProfileAsync(user.Id);
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

        // Process profile picture upload (web concern: IFormFile handling + image resize)
        byte[]? pictureData = null;
        string? pictureContentType = null;
        if (model.ProfilePictureUpload is { Length: > 0 })
        {
            if (model.ProfilePictureUpload.Length > MaxProfilePictureUploadBytes)
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureTooLarge"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                return View(model);
            }

            var uploadContentType = model.ProfilePictureUpload.ContentType;
            if (!AllowedImageContentTypes.Contains(uploadContentType))
            {
                var ext = Path.GetExtension(model.ProfilePictureUpload.FileName);
                if (!string.IsNullOrEmpty(ext) && HeifExtensionToContentType.TryGetValue(ext, out var mapped))
                {
                    uploadContentType = mapped;
                }
            }

            if (!AllowedImageContentTypes.Contains(uploadContentType))
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureInvalidFormat"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                return View(model);
            }

            using var uploadStream = new MemoryStream();
            await model.ProfilePictureUpload.CopyToAsync(uploadStream);
            var result = ResizeProfilePicture(uploadStream.ToArray(), uploadContentType);
            if (result is null)
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureInvalidFormat"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration.GetRequiredSetting(_configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                return View(model);
            }

            pictureData = result.Value.Data;
            pictureContentType = result.Value.ContentType;
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
            ProfilePictureData: pictureData,
            ProfilePictureContentType: pictureContentType,
            RemoveProfilePicture: model.RemoveProfilePicture,
            SelectedTier: isInitialSetup ? model.SelectedTier : null,
            ApplicationMotivation: model.ApplicationMotivation,
            ApplicationAdditionalInfo: model.ApplicationAdditionalInfo,
            ApplicationSignificantContribution: model.ApplicationSignificantContribution,
            ApplicationRoleUnderstanding: model.ApplicationRoleUnderstanding);

        var profileId = await _profileService.SaveProfileAsync(
            user.Id, model.BurnerName, saveRequest,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        // Cancel any pending deletion request when creating a profile
        if (isInitialSetup && user.IsDeletionPending)
        {
            user.DeletionRequestedAt = null;
            user.DeletionScheduledFor = null;
            user.DeletionEligibleAfter = null;
            await UserManager.UpdateAsync(user);
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

        SetSuccess(_localizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(Me));
    }

    [HttpGet("Me/Emails")]
    public async Task<IActionResult> Emails()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(user);
        return View(viewModel);
    }

    [HttpPost("Me/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await GetCurrentUserAsync();
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

            // Build verification URL
            var verificationUrl = Url.Action(
                nameof(VerifyEmail),
                "Profile",
                new { userId = user.Id, token = HttpUtility.UrlEncode(result.Token) },
                Request.Scheme);

            // Send verification email
            await _emailService.SendEmailVerificationAsync(
                model.NewEmail.Trim(),
                user.DisplayName,
                verificationUrl!,
                result.IsConflict,
                user.PreferredLanguage);

            _logger.LogInformation(
                "Sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
                model.NewEmail, user.Id, result.IsConflict);

            if (result.IsConflict)
            {
                SetInfo("This email is linked to another account. Verifying it will request an account merge. Check your inbox for the verification link.");
            }
            else
            {
                SetSuccess(string.Format(CultureInfo.CurrentCulture, _localizer["Profile_VerificationSent"].Value, model.NewEmail.Trim()));
            }
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Rejected email add for user {UserId}: {Reason}", user.Id, ex.Message);
            ModelState.AddModelError(nameof(model.NewEmail), ex.Message);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpGet("Me/Emails/Verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return VerifyEmailError(_localizer["Profile_InvalidVerificationLink"].Value);
        }

        try
        {
            var decodedToken = HttpUtility.UrlDecode(token);
            var result = await _userEmailService.VerifyEmailAsync(userId, decodedToken);
            _cache.InvalidateNobodiesTeamEmails();

            if (result.MergeRequestCreated)
            {
                _logger.LogInformation(
                    "User {UserId} verified email {Email} — merge request created",
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

    private IActionResult VerifyEmailError(string message)
    {
        ViewData["Success"] = false;
        ViewData["Message"] = message;
        return View("VerifyEmailResult");
    }

    [HttpPost("Me/Emails/SetNotificationTarget")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNotificationTarget(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        try
        {
            await _userEmailService.SetNotificationTargetAsync(user.Id, emailId);
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to set notification target {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEmailVisibility(Guid emailId, string? visibility)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        ContactFieldVisibility? parsedVisibility = null;
        if (!string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, ignoreCase: true, out var v))
        {
            parsedVisibility = v;
        }

        try
        {
            await _userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
            SetSuccess(_localizer["Profile_EmailVisibilityUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to set email visibility for email {EmailId} and user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEmail(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        try
        {
            await _userEmailService.DeleteEmailAsync(user.Id, emailId);
            _cache.InvalidateNobodiesTeamEmails();
            SetSuccess(_localizer["Profile_EmailDeleted"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to delete email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost("Me/Emails/SetGoogleService")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGoogleServiceEmail(Guid emailId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        try
        {
            var emailAddress = await _userEmailService.GetVerifiedEmailAddressAsync(user.Id, emailId);

            if (emailAddress is null)
            {
                SetError("Email not found or not verified.");
                return RedirectToAction(nameof(Emails));
            }

            // If they have a @nobodies.team email, it must be used
            var hasNobodiesTeam = await _userEmailService.HasNobodiesTeamEmailAsync(user.Id);

            if (hasNobodiesTeam && !emailAddress.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            {
                SetError("Your @nobodies.team email must be used for Google services.");
                return RedirectToAction(nameof(Emails));
            }

            // null = use OAuth email (default behavior)
            var isOAuthEmail = string.Equals(emailAddress, user.Email, StringComparison.OrdinalIgnoreCase);
            var previousEmail = user.GoogleEmail;
            user.GoogleEmail = isOAuthEmail ? null : emailAddress;
            user.GoogleEmailStatus = GoogleEmailStatus.Unknown;
            await _userManager.UpdateAsync(user);

            // If email changed, enqueue fresh sync events for all current team memberships
            var newEmail = user.GetGoogleServiceEmail();
            if (!string.Equals(previousEmail ?? user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            {
                await _teamService.EnqueueGoogleResyncForUserTeamsAsync(user.Id);
            }

            SetSuccess("Google service email updated. Sync will be retried with the new email.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set Google service email for user {UserId}", user.Id);
            SetError("Failed to update Google service email.");
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpGet("Me/Outbox")]
    public async Task<IActionResult> MyOutbox()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var messages = await _emailOutboxService.GetMessagesForUserAsync(user.Id);

        return View("Outbox", messages);
    }

    [HttpGet("Me/Privacy")]
    public async Task<IActionResult> Privacy()
    {
        var user = await GetCurrentUserAsync();
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
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var result = await _profileService.RequestDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                SetError(_localizer["Profile_DeletionAlreadyPending"].Value);
            return RedirectToAction(nameof(Privacy));
        }

        var deletionDate = user.DeletionScheduledFor?.ToDateTimeUtc();
        SetSuccess(string.Format(CultureInfo.CurrentCulture,
            _localizer["Profile_DeletionRequested"].Value,
            deletionDate.ToDisplayLongDate() ?? ""));
        return RedirectToAction(nameof(Privacy));
    }

    [HttpPost("Me/Privacy/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var result = await _profileService.CancelDeletionAsync(user.Id);
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
            var user = await GetCurrentUserAsync();
            if (user is null)
                return NotFound();

            var profile = await _shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);

            var quirks = profile?.Quirks ?? [];
            var skills = profile?.Skills ?? [];
            var languages = profile?.Languages ?? [];
            var viewModel = new ShiftInfoViewModel
            {
                SelectedSkills = skills.Where(s => !s.StartsWith("Other:", StringComparison.Ordinal)).ToList(),
                SkillOtherText = skills.FirstOrDefault(s => s.StartsWith("Other:", StringComparison.Ordinal))?.Substring(6).Trim(),
                SelectedQuirks = ShiftInfoViewModel.ExtractToggleQuirks(quirks),
                TimePreference = ShiftInfoViewModel.ExtractTimePreference(quirks),
                SelectedLanguages = languages.Where(l => !l.StartsWith("Other:", StringComparison.Ordinal)).ToList(),
                LanguageOtherText = languages.FirstOrDefault(l => l.StartsWith("Other:", StringComparison.Ordinal))?.Substring(6).Trim(),
            };
            // If there was "Other: text" stored, ensure "Other" is in the selected list
            if (viewModel.SkillOtherText is not null && !viewModel.SelectedSkills.Contains("Other", StringComparer.Ordinal))
                viewModel.SelectedSkills.Add("Other");
            if (viewModel.LanguageOtherText is not null && !viewModel.SelectedLanguages.Contains("Other", StringComparer.Ordinal))
                viewModel.SelectedLanguages.Add("Other");

            return View(viewModel);
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
            var user = await GetCurrentUserAsync();
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
            var user = await GetCurrentUserAsync();
            if (user is null)
                return NotFound();

            return View(await BuildCommunicationPreferencesViewModelAsync(user.Id));
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
            var user = await GetCurrentUserAsync();
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
        var user = await GetCurrentUserAsync();
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
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        // Eligibility: must have a Google login, must have a captured avatar URL, and
        // must NOT already have a custom picture (no replacement flow in this PR).
        var externalLogins = await UserManager.GetLoginsAsync(user);
        var hasGoogleLogin = externalLogins.Any(l =>
            string.Equals(l.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase));
        if (!hasGoogleLogin || string.IsNullOrEmpty(user.ProfilePictureUrl))
        {
            SetError(_localizer["Profile_ImportGooglePhoto_Unavailable"].Value);
            return RedirectToAction(nameof(Edit));
        }

        var profile = await _profileService.GetProfileAsync(user.Id, ct);
        if (profile is null)
        {
            SetError(_localizer["Profile_ImportGooglePhoto_NoProfile"].Value);
            return RedirectToAction(nameof(Edit));
        }
        if (profile.HasCustomProfilePicture)
        {
            SetError(_localizer["Profile_ImportGooglePhoto_AlreadyHasCustom"].Value);
            return RedirectToAction(nameof(Edit));
        }

        // SSRF guard: only fetch from Google's avatar host. The URL came from a
        // Google OAuth claim, but we don't trust the stored value blindly — refuse
        // anything that isn't HTTPS and on a *.googleusercontent.com host.
        if (!Uri.TryCreate(user.ProfilePictureUrl, UriKind.Absolute, out var pictureUri)
            || !string.Equals(pictureUri.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || !pictureUri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refusing to import Google photo for user {UserId}: URL is not a trusted Google host",
                user.Id);
            SetError(_localizer["Profile_ImportGooglePhoto_NotGoogleUrl"].Value);
            return RedirectToAction(nameof(Edit));
        }

        byte[] rawBytes;
        string fetchedContentType;
        try
        {
            var httpClient = _httpClientFactory.CreateClient(GoogleAvatarHttpClientName);
            using var response = await httpClient.GetAsync(pictureUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google avatar fetch for user {UserId} returned {StatusCode}",
                    user.Id, (int)response.StatusCode);
                SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return RedirectToAction(nameof(Edit));
            }

            fetchedContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AllowedImageContentTypes.Contains(fetchedContentType))
            {
                _logger.LogWarning(
                    "Google avatar for user {UserId} returned unsupported content type {ContentType}",
                    user.Id, fetchedContentType);
                SetError(_localizer["Profile_ImportGooglePhoto_InvalidFormat"].Value);
                return RedirectToAction(nameof(Edit));
            }

            rawBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (rawBytes.Length == 0 || rawBytes.Length > MaxGooglePhotoDownloadBytes)
            {
                _logger.LogWarning(
                    "Google avatar for user {UserId} had invalid size {Bytes}", user.Id, rawBytes.Length);
                SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return RedirectToAction(nameof(Edit));
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google avatar fetch failed for user {UserId}", user.Id);
            SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
            return RedirectToAction(nameof(Edit));
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Google avatar fetch timed out for user {UserId}", user.Id);
            SetError(_localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
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

    // ─── View Another Profile ────────────────────────────────────────

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ViewProfile(Guid id, CancellationToken ct)
    {
        var profile = await _profileService.GetProfileAsync(id, ct);

        if (profile is null || profile.IsSuspended)
        {
            return NotFound();
        }

        var viewer = await GetCurrentUserAsync();
        if (viewer is null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;

        // Load no-show history for coordinators/NoInfoAdmin/Admin viewing other profiles
        List<NoShowHistoryItem>? noShowHistory = null;
        var viewerCanViewShiftHistory = false;
        if (!isOwnProfile)
        {
            var viewerIsCoordinator = (await _shiftMgmt.GetCoordinatorTeamIdsAsync(viewer.Id)).Count > 0;
            viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);

            if (viewerCanViewShiftHistory)
            {
                var noShows = await _shiftSignupService.GetNoShowHistoryAsync(id);
                if (noShows.Count > 0)
                {
                    var noShowTeamIds = noShows.Select(s => s.Shift.Rota.TeamId).Distinct().ToList();
                    var noShowTeamNames = await _teamService.GetTeamNamesByIdsAsync(noShowTeamIds, ct);

                    var reviewerIds = noShows
                        .Where(s => s.ReviewedByUserId.HasValue)
                        .Select(s => s.ReviewedByUserId!.Value)
                        .Distinct()
                        .ToList();
                    var reviewers = reviewerIds.Count == 0
                        ? (IReadOnlyDictionary<Guid, User>)new Dictionary<Guid, User>()
                        : await _userService.GetByIdsAsync(reviewerIds, ct);

                    noShowHistory = noShows.Select(s =>
                    {
                        var signupEs = s.Shift.Rota.EventSettings;
                        var signupTz = DateTimeZoneProviders.Tzdb[signupEs.TimeZoneId];
                        var shiftStart = s.Shift.GetAbsoluteStart(signupEs);
                        var zoned = shiftStart.InZone(signupTz);
                        var reviewer = s.ReviewedByUserId.HasValue
                            ? reviewers.GetValueOrDefault(s.ReviewedByUserId.Value)
                            : null;
                        return new NoShowHistoryItem
                        {
                            ShiftLabel = s.Shift.Rota.Name,
                            DepartmentName = noShowTeamNames.GetValueOrDefault(s.Shift.Rota.TeamId, ""),
                            ShiftDateLabel = zoned.ToDisplayShortDateTime(),
                            MarkedByName = reviewer?.DisplayName,
                            MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDisplayShortMonthDayTime()
                        };
                    }).ToList();
                }
            }
        }

        // The ProfileCard ViewComponent handles all data fetching and permission checks.
        var profileUser = await _userService.GetByIdAsync(id, ct);
        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profileUser?.DisplayName ?? "Unknown",
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowHistory,
            CanViewShiftSignups = viewerCanViewShiftHistory,
        };

        return View("Index", viewModel);
    }

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id, CancellationToken ct)
    {
        var profile = await _profileService.GetProfileAsync(id, ct);
        if (profile is null) return NotFound();

        var popoverUser = await _userService.GetByIdAsync(id, ct);
        var teams = await _teamService.GetActiveTeamNamesForUserAsync(id, ct);

        var effectivePictureUrl = profile.HasCustomProfilePicture
            ? Url.Action(nameof(Picture), "Profile",
                new { id = profile.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : popoverUser?.ProfilePictureUrl;

        var vm = new ProfileSummaryViewModel
        {
            UserId = id,
            DisplayName = popoverUser?.DisplayName ?? "Unknown",
            Email = popoverUser?.Email,
            ProfilePictureUrl = effectivePictureUrl,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = profile.IsSuspended,
            Teams = teams.ToList()
        };

        return PartialView("_HumanPopover", vm);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        var targetUser = await FindUserByIdAsync(id);
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
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        var targetUser = await _userManager.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (targetUser is null)
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

        // Strip any HTML tags from the message for safety
        var cleanMessage = System.Text.RegularExpressions.Regex.Replace(
            model.Message, "<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        var sender = await _userManager.Users
            .Include(u => u.UserEmails)
            .FirstAsync(u => u.Id == currentUser.Id);

        var recipientEmail = targetUser.GetEffectiveEmail() ?? targetUser.Email;
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }

        var senderEmail = sender.GetEffectiveEmail() ?? sender.Email;
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            ModelState.AddModelError(string.Empty, _localizer["Common_Error"].Value);
            return View(model);
        }

        await _emailService.SendFacilitatedMessageAsync(
            recipientEmail,
            targetUser.DisplayName,
            sender.DisplayName,
            cleanMessage,
            model.IncludeContactInfo,
            senderEmail,
            targetUser.PreferredLanguage);

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

        var results = await _profileService.SearchHumansAsync(q, ct);

        viewModel.Results = results
            .Select(r => r.ToHumanSearchViewModel(Url))
            .ToList();

        return View(viewModel);
    }

    // ─── Admin: All Humans List ──────────────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("Admin")]
    public async Task<IActionResult> AdminList(string? search, string? filter, string sort = "name", string dir = "asc", int page = 1, CancellationToken ct = default)
    {
        var pageSize = 20;
        var allRows = await _profileService.GetFilteredHumansAsync(search, filter, ct);
        var totalCount = allRows.Count;

        // Materialize for flexible sorting (fine at ~500 users)
        // nobodies.team email status is now resolved by NobodiesEmailBadgeViewComponent in the view
        var allMatching = allRows.Select(r => new AdminHumanViewModel
        {
            Id = r.UserId,
            Email = r.Email,
            DisplayName = r.DisplayName,
            ProfilePictureUrl = r.ProfilePictureUrl,
            CreatedAt = r.CreatedAt,
            LastLoginAt = r.LastLoginAt,
            HasProfile = r.HasProfile,
            IsApproved = r.IsApproved,
            MembershipStatus = r.MembershipStatus
        }).ToList();

        var ascending = !string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        IEnumerable<AdminHumanViewModel> sorted = sort?.ToLowerInvariant() switch
        {
            "joined" => ascending
                ? allMatching.OrderBy(m => m.CreatedAt)
                : allMatching.OrderByDescending(m => m.CreatedAt),
            "login" => ascending
                ? allMatching.OrderBy(m => m.LastLoginAt.HasValue ? 0 : 1).ThenBy(m => m.LastLoginAt)
                : allMatching.OrderBy(m => m.LastLoginAt.HasValue ? 0 : 1).ThenByDescending(m => m.LastLoginAt),
            "status" => ascending
                ? allMatching.OrderBy(m => m.MembershipStatus, StringComparer.OrdinalIgnoreCase)
                : allMatching.OrderByDescending(m => m.MembershipStatus, StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? allMatching.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                : allMatching.OrderByDescending(m => m.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        var members = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var viewModel = new AdminHumanListViewModel
        {
            Humans = members,
            SearchTerm = search,
            StatusFilter = filter,
            SortBy = sort?.ToLowerInvariant() ?? "name",
            SortDir = ascending ? "asc" : "desc",
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("AdminList", viewModel);
    }

    // ─── Admin: Per-Person Detail ────────────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin")]
    public async Task<IActionResult> AdminDetail(Guid id, CancellationToken ct)
    {
        var data = await _profileService.GetAdminHumanDetailAsync(id, ct);
        if (data is null)
        {
            return NotFound();
        }

        var campaignGrants = await _campaignService.GetAllGrantsForUserAsync(id, ct);
        ViewBag.CampaignGrants = campaignGrants;

        var outboxCount = await _emailOutboxService.GetMessageCountForUserAsync(id, ct);
        ViewBag.OutboxCount = outboxCount;

        var profileLanguages = data.Profile is not null
            ? await _profileService.GetProfileLanguagesAsync(data.Profile.Id, ct)
            : (IReadOnlyList<ProfileLanguage>)[];

        var now = _clock.GetCurrentInstant();

        var effectiveEmail = data.UserEmails
            .FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email
            ?? data.User.Email;

        var viewModel = new AdminHumanDetailViewModel
        {
            UserId = data.User.Id,
            Email = effectiveEmail ?? string.Empty,
            DisplayName = data.User.DisplayName,
            ProfilePictureUrl = data.User.ProfilePictureUrl,
            CreatedAt = data.User.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = data.User.LastLoginAt?.ToDateTimeUtc(),
            IsSuspended = data.Profile?.IsSuspended ?? false,
            IsApproved = data.Profile?.IsApproved ?? false,
            HasProfile = data.Profile is not null,
            AdminNotes = data.Profile?.AdminNotes,
            PreferredLanguage = data.User.PreferredLanguage,
            MembershipTier = data.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = data.Profile?.ConsentCheckStatus,
            IsRejected = data.Profile?.RejectedAt is not null,
            RejectionReason = data.Profile?.RejectionReason,
            RejectedAt = data.Profile?.RejectedAt?.ToDateTimeUtc(),
            RejectedByName = data.RejectedByName,
            ApplicationCount = data.Applications.Count,
            ConsentCount = data.ConsentCount,
            Applications = data.Applications
                .Take(5)
                .Select(a => new AdminHumanApplicationViewModel
                {
                    Id = a.Id,
                    Status = a.Status,
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc()
                }).ToList(),
            RoleAssignments = data.RoleAssignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByUser?.DisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            Languages = profileLanguages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = Helpers.LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            OAuthEmail = data.User.Email,
            GoogleServiceEmail = data.User.GetGoogleServiceEmail(),
            GoogleEmailStatus = data.User.GoogleEmailStatus,
            UserEmails = data.UserEmails
                .OrderBy(e => e.DisplayOrder)
                .Select(e => new AdminUserEmailViewModel
                {
                    Email = e.Email,
                    IsOAuth = e.IsOAuth,
                    IsVerified = e.IsVerified,
                    IsNotificationTarget = e.IsNotificationTarget,
                    Visibility = e.Visibility,
                }).ToList(),
        };

        // nobodies.team email is now resolved by NobodiesEmailBadgeViewComponent in the view

        return View("AdminDetail", viewModel);
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
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.SuspendAsync(id, currentUser.Id, notes);
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
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.UnsuspendAsync(id, currentUser.Id);
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
        var currentUser = await GetCurrentUserAsync();
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
        var currentUser = await GetCurrentUserAsync();
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
        var user = await FindUserByIdAsync(id);
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
        var user = await FindUserByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            model.UserId = id;
            model.UserDisplayName = user.DisplayName;
            model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
            return View(model);
        }

        var currentUser = await GetCurrentUserAsync();
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

        if (!result.Success)
        {
            SetError(string.Format(_localizer["Admin_RoleAlreadyActive"].Value, model.RoleName));
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        SetSuccess(string.Format(_localizer["Admin_RoleAssigned"].Value, model.RoleName));
        return RedirectToAction(nameof(AdminDetail), new { id });
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

        var currentUser = await GetCurrentUserAsync();
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

        if (!result.Success)
        {
            SetError(_localizer["Admin_RoleNotActive"].Value);
            return RedirectToAction(nameof(AdminDetail), new { id = roleAssignment.UserId });
        }

        SetSuccess(string.Format(_localizer["Admin_RoleEnded"].Value, roleAssignment.RoleName, roleAssignment.User.DisplayName));
        return RedirectToAction(nameof(AdminDetail), new { id = roleAssignment.UserId });
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData, string contentType) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, _logger);

    private async Task<EmailsViewModel> BuildEmailsViewModelAsync(User user)
    {
        var emails = await _userEmailService.GetUserEmailsAsync(user.Id);

        var canAdd = true;
        var minutesUntilResend = 0;

        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail is not null)
        {
            var (cooldownCanAdd, cooldownMinutes, _) =
                await _profileService.GetEmailCooldownInfoAsync(pendingEmail.Id);
            canAdd = cooldownCanAdd;
            minutesUntilResend = cooldownMinutes;
        }

        var hasNobodiesTeam = emails.Any(e => e.IsVerified &&
            e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        if (hasNobodiesTeam && user.GoogleEmail is null)
            await _userEmailService.TryBackfillGoogleEmailAsync(user.Id);

        return new EmailsViewModel
        {
            Emails = emails.Select(e => new EmailRowViewModel
            {
                Id = e.Id,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsOAuth = e.IsOAuth,
                IsNotificationTarget = e.IsNotificationTarget,
                Visibility = e.Visibility,
                IsPendingVerification = e.IsPendingVerification,
                IsMergePending = e.IsMergePending,
                IsGoogleServiceEmail = user.GoogleEmail is not null
                    ? string.Equals(e.Email, user.GoogleEmail, StringComparison.OrdinalIgnoreCase)
                    : e.IsOAuth,
                IsNobodiesTeamDomain = e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase)
            }).ToList(),
            CanAddEmail = canAdd,
            MinutesUntilResend = minutesUntilResend,
            GoogleServiceEmail = user.GoogleEmail,
            HasNobodiesTeamEmail = hasNobodiesTeam,
            GoogleEmailStatus = user.GoogleEmailStatus
        };
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        // Check if user is a matched ticket attendee (locks ticketing preference)
        var hasTicketOrder = await _ticketQueryService.HasTicketAttendeeMatchAsync(userId);

        var categories = new List<CategoryPreferenceItem>();

        foreach (var category in MessageCategoryExtensions.ActiveCategories)
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var isAlwaysOn = category.IsAlwaysOn();
            var isTicketingLocked = category == MessageCategory.Ticketing && hasTicketOrder;

            categories.Add(new CategoryPreferenceItem
            {
                Category = category,
                DisplayName = category == MessageCategory.Ticketing
                    ? $"Ticketing — {_clock.GetCurrentInstant().InUtc().Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                EmailEnabled = pref is null || !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket for this year" : null,
            });
        }

        return new CommunicationPreferencesViewModel { Categories = categories };
    }

}
