using Humans.Consent.Contracts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Users.Services;
using Humans.Base.Models.Tables;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using System.Web;
using AngleSharp.Dom;
using Humans.Users.Authorization;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Base;
using Humans.Base.Authorization;

using Humans.GoogleIntegration.Contracts;

namespace Humans.Users.Controllers;

// Own profile only. Own email addresses live in ProfileEmailsController; other members'
// profiles, popovers, messaging and search live in ProfileViewController. All three share
// the /Profile route prefix.
[Authorize]
[Route("Profile")]
internal sealed class ProfileController(
    IUserManagement userService,
    UserManager<User> userManager,
    IProfileEditorService profileEditorService,
    IContactFieldService contactFieldService,
    ICommunicationPreferenceService commPrefService,
    IOnboardingIntake onboardingService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<UsersResource> localizer,
    // The generic Common_/Validation_/Profile_ strings this controller formats stayed in
    // SharedResource at the Users carve.
    IStringLocalizer<SharedResource> sharedLocalizer,
    IGovernanceService governanceService,
    IAccountDeletionService accountDeletionService,
    IMembershipCalculator membershipCalculator,
    IConsentService consentService) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

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
        var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(info.Id, ct);
        var pendingConsentCount = snapshot.PendingConsentCount;

        var applications = await governanceService.GetUserApplicationsAsync(info.Id, ct);
        var latestApplication = applications.MaxBy(a => a.SubmittedAt);
        var review = await consentService.GetConsentReviewAsync(info.Id, ct);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = info.Id,
            HasPendingConsents = pendingConsentCount > 0,
            PendingConsentCount = pendingConsentCount,
            IsApproved = profile is not null && review.IsApproved,
            IsOwnProfile = true,
            DisplayName = info.BurnerName,
        };

        // Tier app status (skip Withdrawn).
        if (latestApplication is not null && latestApplication.Status != ApplicationStatus.Withdrawn)
        {
            viewModel.TierApplicationStatus = latestApplication.Status;
            viewModel.TierApplicationTier = latestApplication.MembershipTier;
            viewModel.TierApplicationBadgeClass = EnumBadgeMap.For(latestApplication.Status);
        }

        return View("Index", viewModel);
    }

    [HttpGet("Me/Edit")]
    public async Task<IActionResult> Edit([FromQuery] bool preview = false, CancellationToken ct = default)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info is null) return NotFound();

        var applications = await governanceService.GetUserApplicationsAsync(user.Id, ct);
        var review = await consentService.GetConsentReviewAsync(info.Id, ct);
        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            review.IsApproved,
            preview,
            p => Url.Action(nameof(ProfileViewController.Picture), "ProfileView", new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(21 * 1024 * 1024)] // 20 MB picture plus multipart overhead; upload validation enforces the file cap.
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return NotFound();
        model.ContactFields = ProfileEditViewModelBuilder.BuildLegacyContactFields(user.Profile);

        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        ValidateEditSimpleFieldGuards(model);
        if (ModelState.ErrorCount > 0)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // The Consent Coordinator's approval (Consent's review): "not approved yet" IS initial
        // setup (it already absorbs the no-profile case).
        var isInitialSetup = user.Profile is null
            || !(await consentService.GetConsentReviewAsync(user.Id)).IsApproved;

        var burnerCvResult = ValidateBurnerCvOrNull(model, isInitialSetup);
        if (burnerCvResult is not null)
            return burnerCvResult;

        var pictureUpload = await TryReadProfilePictureUploadAsync(model);
        if (!pictureUpload.Success)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var (profileId, saveResult) = await SaveEditedProfileOrNullAsync(model, user.Id, isInitialSetup, pictureUpload);
        if (saveResult is not null)
            return saveResult;

        // Cross-section peer calls (consent-check trigger, pending-deletion
        // cancel) stay controller-side per
        // no-leaf-to-director-callbacks — the controller is the director,
        // services are peers/leaves that must not call each other back.
        await RunPostProfileSaveOrchestrationAsync(user, isInitialSetup);

        await SaveEditedContactFieldsAsync(model, profileId);

        // Languages: remove-and-replace.
        var newLanguages = model.EditableLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
            .Select(l => new ProfileLanguageInfo(
                Guid.NewGuid(),
                l.LanguageCode.Trim(),
                l.Proficiency))
            .ToList();

        await _userService.SaveProfileLanguagesAsync(profileId, newLanguages);

        SetSuccess(sharedLocalizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(Me));
    }

    // Simple field-level guards (phone E.164 format) —
    // localized, form-field-targeted, so they stay controller-side alongside
    // the ModelState they populate.
    private void ValidateEditSimpleFieldGuards(ProfileViewModel model)
    {
        var phoneTypes = new[] { ContactFieldType.Phone, ContactFieldType.WhatsApp };
        for (var i = 0; i < model.EditableContactFields.Count; i++)
        {
            var cf = model.EditableContactFields[i];
            if (!string.IsNullOrWhiteSpace(cf.Value) && phoneTypes.Contains(cf.FieldType) && !cf.Value.TrimStart().StartsWith("+", StringComparison.Ordinal))
            {
                ModelState.AddModelError($"EditableContactFields[{i}].Value",
                    sharedLocalizer["Validation_PhoneE164", localizer["Profile_" + cf.FieldType].Value].Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(model.EmergencyContactPhone) && !model.EmergencyContactPhone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.EmergencyContactPhone),
                sharedLocalizer["Validation_PhoneE164", localizer["Profile_EmergencyContactPhone"].Value].Value);
        }
    }

    // Burner CV: entries OR "no prior experience". Returns the rerender result
    // when invalid, null to continue.
    private IActionResult? ValidateBurnerCvOrNull(ProfileViewModel model, bool isInitialSetup)
    {
        var hasVolunteerHistory = model.EditableVolunteerHistory
            .Any(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue);
        if (!model.NoPriorBurnExperience && !hasVolunteerHistory)
        {
            ModelState.AddModelError(nameof(model.NoPriorBurnExperience),
                localizer["Profile_BurnerCVRequired"].Value);
            model.IsInitialSetup = isInitialSetup;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        return null;
    }

    // CV: existing rows keep Id/CreatedAt; new rows post Guid.Empty and get fresh Id.
    private static ProfileSaveRequest BuildProfileSaveRequest(
        ProfileViewModel model, (bool Success, byte[]? Data, string? ContentType) pictureUpload)
    {
        var cvEntries = model.EditableVolunteerHistory
            .Where(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue)
            .Select(vh => new CVEntry(
                vh.Id ?? Guid.Empty,
                vh.ParsedDate!.Value,
                vh.EventName,
                vh.Description
            ))
            .ToList();

        return new ProfileSaveRequest(
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
            RemoveProfilePicture: model.RemoveProfilePicture,
            VolunteerHistory: cvEntries);
    }

    private async Task<(Guid ProfileId, IActionResult? EarlyReturn)> SaveEditedProfileOrNullAsync(
        ProfileViewModel model, Guid userId, bool isInitialSetup, (bool Success, byte[]? Data, string? ContentType) pictureUpload)
    {
        var saveRequest = BuildProfileSaveRequest(model, pictureUpload);

        var result = await profileEditorService.SaveProfileAsync(
            userId, model.BurnerName, saveRequest);
        if (result.Succeeded) return (result.ProfileId!.Value, null);
        var errorKey = result.ErrorKey;

        ModelState.AddModelError(string.Empty, errorKey is not null
            ? localizer[errorKey].Value : sharedLocalizer["Common_Error"].Value);
        model.IsInitialSetup = isInitialSetup;
        model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
            && string.IsNullOrEmpty(model.LastName)
            && string.IsNullOrEmpty(model.EmergencyContactName);
        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return (Guid.Empty, View(model));
    }

    private async Task RunPostProfileSaveOrchestrationAsync(UserInfo user, bool isInitialSetup)
    {
        // Peer-call into Onboarding; ProfileEditorService doesn't.
        await onboardingService.SetConsentCheckPendingIfEligibleAsync(user.Id);

        // Route pending-deletion cancel through IAccountDeletionService, not raw UserManager.
        if (isInitialSetup && user.IsDeletionPending)
        {
            await accountDeletionService.CancelDeletionAsync(user.Id);
            logger.LogInformation(
                "Cancelled pending deletion request for user {UserId} on profile creation",
                user.Id);
        }
    }

    private async Task SaveEditedContactFieldsAsync(ProfileViewModel model, Guid profileId)
    {
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

        await contactFieldService.SaveContactFieldsAsync(profileId, contactFieldDtos,
            removedLegacyContactFieldIds: model.RemovedLegacyContactFieldIds.Count > 0 ? model.RemovedLegacyContactFieldIds : null);
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
                localizer["Profile_PictureTooLarge"].Value);
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
                localizer["Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        using var uploadStream = new MemoryStream();
        await upload.CopyToAsync(uploadStream, HttpContext.RequestAborted);
        var result = ResizeProfilePicture(uploadStream.ToArray());
        if (result is null)
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                localizer["Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        return (true, result.Value.Data, result.Value.ContentType);
    }

    // Medical conditions are GDPR Art. 9 data: owner-only here (every action acts on the signed-in
    // member), and elsewhere shown only behind MedicalDataViewer. The food answers that used to share
    // this form are Cantina's, at /Profile/Me/Dietary.
    [HttpGet("Me/Medical")]
    public async Task<IActionResult> Medical(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        return View(new MedicalViewModel { MedicalConditions = user.Profile?.MedicalConditions });
    }

    [HttpPost("Me/Medical")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Medical(MedicalViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            await _userService.SaveMedicalConditionsAsync(user.Id,
                string.IsNullOrWhiteSpace(model.MedicalConditions) ? null : model.MedicalConditions.Trim());
            SetSuccess(localizer["Users_Medical_Saved"].Value);
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save medical info");
            SetError(localizer["Users_Medical_SaveFailed"].Value);
            return View(model);
        }
    }

    [HttpGet("Me/CommunicationPreferences")]
    public async Task<IActionResult> CommunicationPreferences()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
            if (user is null)
                return NotFound();

            return View(model: user.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load communication preferences");
            SetError(localizer["Users_Profile_CommunicationPreferencesLoadFailed"].Value);
            return RedirectToAction(nameof(Me));
        }
    }

    [HttpPost("Me/CommunicationPreferences/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, [BindRequired] bool emailEnabled, [BindRequired] bool alertEnabled)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return Unauthorized();
            if (!ModelState.IsValid)
            {
                logger.LogWarning("Rejected malformed communication preference update for {UserId}", user.Id);
                return BadRequest(ModelState);
            }

            if (category.IsAlwaysOn())
                return BadRequest(localizer["Users_Profile_AlwaysOnCategoryCannotChange"].Value);

            await commPrefService.UpdatePreferenceAsync(
                user.Id, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Profile", actorUserId: user.Id);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }

    [HttpGet("Me/Notifications")]
    public IActionResult Notifications() => RedirectToActionPermanent(nameof(CommunicationPreferences));

    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, logger);
}
