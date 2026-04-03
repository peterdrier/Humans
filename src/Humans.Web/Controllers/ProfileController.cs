// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Humans.Application.Configuration;
using Humans.Application.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Profile")]
public class ProfileController : HumansControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly VolunteerHistoryService _volunteerHistoryService;
    private readonly IEmailService _emailService;
    private readonly IUserEmailService _userEmailService;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IConfiguration _configuration;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly ILogger<ProfileController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;

    private const int MaxProfilePictureUploadBytes = 20 * 1024 * 1024; // 20MB upload limit
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
        VolunteerHistoryService volunteerHistoryService,
        IEmailService emailService,
        IUserEmailService userEmailService,
        ICommunicationPreferenceService commPrefService,
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftMgmt,
        IConfiguration configuration,
        ConfigurationRegistry configRegistry,
        ILogger<ProfileController> logger,
        IStringLocalizer<SharedResource> localizer,
        HumansDbContext dbContext,
        IMemoryCache cache,
        IClock clock)
        : base(userManager)
    {
        _userManager = userManager;
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _volunteerHistoryService = volunteerHistoryService;
        _emailService = emailService;
        _userEmailService = userEmailService;
        _commPrefService = commPrefService;
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _roleAssignmentService = roleAssignmentService;
        _shiftSignupService = shiftSignupService;
        _shiftMgmt = shiftMgmt;
        _configuration = configuration;
        _configRegistry = configRegistry;
        _logger = logger;
        _localizer = localizer;
        _dbContext = dbContext;
        _cache = cache;
        _clock = clock;
    }

    // ─── Own Profile (Me) ────────────────────────────────────────────

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Me));

    [HttpGet("Me")]
    public async Task<IActionResult> Me()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var (profile, latestApplication, pendingConsentCount) =
            await _profileService.GetProfileIndexDataAsync(user.Id);
        var campaignGrants = await _profileService.GetActiveOrCompletedCampaignGrantsAsync(user.Id);

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
    public async Task<IActionResult> Edit([FromQuery] bool preview = false)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var (profile, isTierLocked, pendingApplication) =
            await _profileService.GetProfileEditDataAsync(user.Id);

        // Get all contact fields for editing
        var contactFields = profile is not null
            ? await _contactFieldService.GetAllContactFieldsAsync(profile.Id)
            : [];

        // Get all volunteer history entries for editing
        var volunteerHistory = profile is not null
            ? await _volunteerHistoryService.GetAllAsync(profile.Id)
            : [];

        var hasCustomPicture = profile?.HasCustomProfilePicture == true;

        // Initial setup = no profile or not yet approved (onboarding)
        // ?preview=true forces initial-setup mode for testing
        var isInitialSetup = profile is null || !profile.IsApproved || preview;

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture
                ? Url.Action(nameof(Picture), new { id = profile!.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
                : null,
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
            EditableVolunteerHistory = volunteerHistory.Select(vh => new VolunteerHistoryEntryEditViewModel
            {
                Id = vh.Id,
                DateString = vh.Date.ToIsoDateString(),
                EventName = vh.EventName,
                Description = vh.Description
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

        // Save volunteer history entries
        var volunteerHistoryDtos = model.EditableVolunteerHistory
            .Where(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue)
            .Select(vh => new VolunteerHistoryEntryEditDto(
                vh.Id,
                vh.ParsedDate!.Value,
                vh.EventName,
                vh.Description
            ))
            .ToList();

        await _volunteerHistoryService.SaveAsync(profileId, volunteerHistoryDtos);

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
            _logger.LogWarning(ex, "Failed to add email address for user {UserId}", user.Id);
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
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);

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
            _logger.LogWarning(ex, "Email verification failed for user {UserId}", userId);
            return VerifyEmailError(_localizer["Profile_InvalidVerificationLink"].Value);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning(ex, "Email verification validation failed for user {UserId}", userId);
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
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);
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
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);
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
            var email = await _dbContext.UserEmails
                .FirstOrDefaultAsync(ue => ue.Id == emailId && ue.UserId == user.Id && ue.IsVerified);

            if (email is null)
            {
                SetError("Email not found or not verified.");
                return RedirectToAction(nameof(Emails));
            }

            // If they have a @nobodies.team email, it must be used
            var hasNobodiesTeam = await _userEmailService.HasNobodiesTeamEmailAsync(user.Id);

            if (hasNobodiesTeam && !email.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            {
                SetError("Your @nobodies.team email must be used for Google services.");
                return RedirectToAction(nameof(Emails));
            }

            // null = use OAuth email (default behavior)
            var previousEmail = user.GoogleEmail;
            user.GoogleEmail = email.IsOAuth ? null : email.Email;
            user.GoogleEmailStatus = GoogleEmailStatus.Unknown;
            await _userManager.UpdateAsync(user);

            // If email changed, enqueue fresh sync events for all current team memberships
            var newEmail = user.GetGoogleServiceEmail();
            if (!string.Equals(previousEmail ?? user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            {
                await EnqueueResyncForUserTeamsAsync(user.Id);
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

        var messages = await _dbContext.EmailOutboxMessages
            .Where(m => m.UserId == user.Id)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

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

        // Reload user to get updated deletion date
        await GetCurrentUserAsync();
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

            var profile = await _profileService.GetShiftProfileAsync(user.Id, includeMedical: false);

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

            var shiftProfile = await _profileService.GetOrCreateShiftProfileAsync(user.Id);

            shiftProfile.Skills = ShiftInfoViewModel.MergeSkills(
                model.SelectedSkills, model.SkillOtherText, shiftProfile.Skills);
            shiftProfile.Quirks = ShiftInfoViewModel.MergePersistedQuirks(
                model.TimePreference, model.SelectedQuirks, shiftProfile.Quirks);
            shiftProfile.Languages = ShiftInfoViewModel.MergeLanguages(
                model.SelectedLanguages, model.LanguageOtherText, shiftProfile.Languages);

            await _profileService.UpdateShiftProfileAsync(shiftProfile);

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

    [HttpGet("Me/Notifications")]
    public async Task<IActionResult> Notifications()
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
            _logger.LogError(ex, "Failed to load notification settings");
            SetError("Failed to load notification settings.");
            return RedirectToAction(nameof(Me));
        }
    }

    [HttpPost("Me/Notifications")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Notifications(CommunicationPreferencesViewModel model)
    {
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
                return NotFound();

            foreach (var item in model.Categories)
            {
                if (item.Category == MessageCategory.System)
                    continue;

                await _commPrefService.UpdatePreferenceAsync(
                    user.Id, item.Category, item.OptedOut, "Profile");
            }

            SetSuccess(_localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(Notifications));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save notification settings");
            SetError("Failed to save notification settings.");
            PopulateCommunicationPreferenceMetadata(model);
            return View(model);
        }
    }

    [HttpGet("Me/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var exportData = await _profileService.ExportDataAsync(user.Id);

        var json = System.Text.Json.JsonSerializer.Serialize(exportData, ExportJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"nobodies-profiles-export-{DateTime.UtcNow.ToIsoDateString()}.json";

        return File(bytes, "application/json", fileName);
    }

    // ─── Shared (Profile Picture) ────────────────────────────────────

    [HttpGet("Picture")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id)
    {
        var (data, contentType) = await _profileService.GetProfilePictureAsync(id);

        if (data is null || string.IsNullOrEmpty(contentType))
            return NotFound();

        return File(data, contentType);
    }

    // ─── View Another Profile ────────────────────────────────────────

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ViewProfile(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);

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
        if (!isOwnProfile)
        {
            var viewerIsCoordinator = (await _shiftMgmt.GetCoordinatorTeamIdsAsync(viewer.Id)).Count > 0;
            var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);

            if (viewerCanViewShiftHistory)
            {
                var noShows = await _shiftSignupService.GetNoShowHistoryAsync(id);
                if (noShows.Count > 0)
                {
                    noShowHistory = noShows.Select(s =>
                    {
                        var signupEs = s.Shift.Rota.EventSettings;
                        var signupTz = DateTimeZoneProviders.Tzdb[signupEs.TimeZoneId];
                        var shiftStart = s.Shift.GetAbsoluteStart(signupEs);
                        var zoned = shiftStart.InZone(signupTz);
                        return new NoShowHistoryItem
                        {
                            ShiftLabel = s.Shift.Rota.Name,
                            DepartmentName = s.Shift.Rota.Team?.Name ?? "",
                            ShiftDateLabel = zoned.ToDisplayShortDateTime(),
                            MarkedByName = s.ReviewedByUser?.DisplayName,
                            MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDisplayShortMonthDayTime()
                        };
                    }).ToList();
                }
            }
        }

        // The ProfileCard ViewComponent handles all data fetching and permission checks.
        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profile.User.DisplayName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowHistory,
        };

        return View("Index", viewModel);
    }

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id)
    {
        var profile = await _profileService.GetProfileAsync(id);
        if (profile is null || profile.IsSuspended) return NotFound();

        var teams = await _dbContext.TeamMembers
            .Where(tm => tm.UserId == id && tm.LeftAt == null
                && tm.Team!.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.Team!.Name)
            .OrderBy(n => n)
            .ToListAsync();

        var effectivePictureUrl = profile.HasCustomProfilePicture
            ? Url.Action(nameof(Picture), "Profile",
                new { id = profile.Id, v = profile.UpdatedAt.ToUnixTimeTicks() })
            : profile.User.ProfilePictureUrl;

        var vm = new ProfileSummaryViewModel
        {
            UserId = id,
            DisplayName = profile.User.DisplayName,
            Email = profile.User.Email,
            ProfilePictureUrl = effectivePictureUrl,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            Teams = teams
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
            currentUser.Id, currentUser.DisplayName);

        SetSuccess(string.Format(
            _localizer["SendMessage_Success"].Value,
            targetUser.DisplayName));

        return RedirectToAction(nameof(ViewProfile), new { id });
    }

    // ─── Search ──────────────────────────────────────────────────────

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q)
    {
        var viewModel = new HumanSearchViewModel { Query = q };

        if (!q.HasSearchTerm())
        {
            return View(viewModel);
        }

        var results = await _profileService.SearchHumansAsync(q);

        viewModel.Results = results
            .Select(r => r.ToHumanSearchViewModel(Url))
            .ToList();

        return View(viewModel);
    }

    // ─── Admin: All Humans List ──────────────────────────────────────

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpGet("Admin")]
    public async Task<IActionResult> AdminList(string? search, string? filter, string sort = "name", string dir = "asc", int page = 1)
    {
        var pageSize = 20;
        var allRows = await _profileService.GetFilteredHumansAsync(search, filter);
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

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin")]
    public async Task<IActionResult> AdminDetail(Guid id)
    {
        var data = await _profileService.GetAdminHumanDetailAsync(id);
        if (data is null)
        {
            return NotFound();
        }

        var campaignGrants = await _dbContext.CampaignGrants
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == id)
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync();
        ViewBag.CampaignGrants = campaignGrants;

        var outboxCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.UserId == id);
        ViewBag.OutboxCount = outboxCount;

        var now = _clock.GetCurrentInstant();

        var viewModel = new AdminHumanDetailViewModel
        {
            UserId = data.User.Id,
            Email = data.User.Email ?? string.Empty,
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
        };

        // nobodies.team email is now resolved by NobodiesEmailBadgeViewComponent in the view

        return View("AdminDetail", viewModel);
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id)
    {
        var messages = await _dbContext.EmailOutboxMessages
            .Where(m => m.UserId == id)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        ViewBag.HumanId = id;
        return View("Outbox", messages);
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SuspendHuman(Guid id, string? notes)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.SuspendAsync(id, currentUser.Id, currentUser.DisplayName, notes);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberSuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Unsuspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsuspendHuman(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.UnsuspendAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_MemberUnsuspended"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveVolunteer(Guid id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return NotFound();

        var result = await _onboardingService.ApproveVolunteerAsync(id, currentUser.Id, currentUser.DisplayName);
        if (!result.Success)
            return NotFound();

        SetSuccess(_localizer["Admin_VolunteerApproved"].Value);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSignup(Guid id, string? reason)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
            return Unauthorized();

        var result = await _onboardingService.RejectSignupAsync(id, currentUser.Id, currentUser.DisplayName, reason);
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

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
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

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
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

        // Enforce role assignment authorization
        if (!RoleChecks.CanManageRole(User, model.RoleName))
        {
            return Forbid();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var result = await _roleAssignmentService.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, currentUser.DisplayName, model.Notes);

        if (!result.Success)
        {
            SetError(string.Format(_localizer["Admin_RoleAlreadyActive"].Value, model.RoleName));
            return RedirectToAction(nameof(AdminDetail), new { id });
        }

        SetSuccess(string.Format(_localizer["Admin_RoleAssigned"].Value, model.RoleName));
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await _roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment is null)
        {
            return NotFound();
        }

        // Enforce role assignment authorization
        if (!RoleChecks.CanManageRole(User, roleAssignment.RoleName))
        {
            return Forbid();
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var result = await _roleAssignmentService.EndRoleAsync(
            roleId, currentUser.Id, currentUser.DisplayName, notes);

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

    /// <summary>
    /// Enqueues fresh AddUserToTeamResources sync events for all teams the user is currently a member of.
    /// Used when the Google service email changes to trigger re-sync with the updated email.
    /// </summary>
    private async Task EnqueueResyncForUserTeamsAsync(Guid userId)
    {
        var memberships = await _dbContext.TeamMembers
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Select(tm => new { tm.Id, tm.TeamId })
            .ToListAsync();

        var now = _clock.GetCurrentInstant();
        foreach (var membership in memberships)
        {
            var dedupeKey = $"{membership.Id}:{GoogleSyncOutboxEventTypes.AddUserToTeamResources}:resync:{now}";
            _dbContext.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = membership.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = dedupeKey
            });
        }

        await _dbContext.SaveChangesAsync();

        if (memberships.Count > 0)
        {
            _logger.LogInformation(
                "Enqueued {Count} re-sync events for user {UserId} after Google email change",
                memberships.Count, userId);
        }
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesAsync(userId);
        return new CommunicationPreferencesViewModel
        {
            Categories = prefs.Select(p => new CategoryPreferenceItem
            {
                Category = p.Category,
                DisplayName = p.Category.ToDisplayName(),
                Description = p.Category.ToDescription(),
                OptedOut = p.OptedOut,
                InboxEnabled = p.InboxEnabled,
                IsEditable = p.Category != MessageCategory.System,
            }).ToList()
        };
    }

    private static void PopulateCommunicationPreferenceMetadata(CommunicationPreferencesViewModel model)
    {
        foreach (var item in model.Categories)
        {
            item.DisplayName = item.Category.ToDisplayName();
            item.Description = item.Category.ToDescription();
            item.IsEditable = item.Category != MessageCategory.System;
        }
    }
}
