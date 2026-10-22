using Humans.Consent.Contracts;
using Humans.Users.Services;
using Humans.Base.Models.Tables;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Users.Models;
using Humans.Onboarding.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Base;


namespace Humans.Users.Controllers;

[Authorize]
[Route("Profile")]
internal sealed class ProfileController(
    IUserManagement userService,
    UserManager<User> userManager,
    IProfileEditorService profileEditorService,
    IContactFieldService contactFieldService,
    IOnboardingIntake onboardingService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<UsersResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer,
    IGovernanceService governanceService,
    IMembershipCalculator membershipCalculator,
    IConsentService consentService) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

    private const int MaxProfilePictureUploadBytes = 20 * 1024 * 1024;
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

        // Not yet approved by the Consent Coordinator is initial setup (it covers the no-profile case too).
        var isInitialSetup = user.Profile is null
            || !(await consentService.GetConsentReviewAsync(user.Id)).IsApproved;

        var pictureUpload = await TryReadProfilePictureUploadAsync(model);
        if (!pictureUpload.Success)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var (profileId, saveResult) = await SaveEditedProfileOrNullAsync(model, user.Id, isInitialSetup, pictureUpload);
        if (saveResult is not null)
            return saveResult;

        // ProfileEditorService must not call Onboarding back (no leaf-to-director callbacks), so the controller
        // reports the save.
        await onboardingService.ProfileSavedAsync(user.Id);

        await SaveEditedContactFieldsAsync(model, profileId);

        // Remove and replace.
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

    // Localized and tied to form fields, so these stay beside the ModelState they fill.
    private void ValidateEditSimpleFieldGuards(ProfileViewModel model)
    {
        var phoneTypes = new[] { ContactFieldType.Phone, ContactFieldType.WhatsApp };
        for (var i = 0; i < model.EditableContactFields.Count; i++)
        {
            var cf = model.EditableContactFields[i];
            if (!string.IsNullOrWhiteSpace(cf.Value) && phoneTypes.Contains(cf.FieldType) && !cf.Value.TrimStart().StartsWith("+", StringComparison.Ordinal))
            {
                ModelState.AddModelError($"EditableContactFields[{i}].Value",
                    sharedLocalizer["Validation_PhoneE164", localizer["Users_Profile_" + cf.FieldType].Value].Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(model.EmergencyContactPhone) && !model.EmergencyContactPhone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.EmergencyContactPhone),
                sharedLocalizer["Validation_PhoneE164", localizer["Users_Profile_EmergencyContactPhone"].Value].Value);
        }
    }

    // CV: existing rows keep Id and CreatedAt; new rows post Guid.Empty and get a fresh Id.
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
            userId, saveRequest);
        if (result.Succeeded) return (result.ProfileId!.Value, null);
        var errorKey = result.ErrorKey;

        ModelState.AddModelError(
            string.Equals(errorKey, ProfileEditorService.BurnerCvRequiredErrorKey, StringComparison.Ordinal) ? nameof(model.NoPriorBurnExperience) : string.Empty,
            errorKey is not null ? localizer[errorKey].Value : sharedLocalizer["Common_Error"].Value);
        model.IsInitialSetup = isInitialSetup;
        model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
            && string.IsNullOrEmpty(model.LastName)
            && string.IsNullOrEmpty(model.EmergencyContactName);
        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return (Guid.Empty, View(model));
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
                localizer["Users_Profile_PictureTooLarge"].Value);
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
                localizer["Users_Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        using var uploadStream = new MemoryStream();
        await upload.CopyToAsync(uploadStream, HttpContext.RequestAborted);
        var result = ResizeProfilePicture(uploadStream.ToArray());
        if (result is null)
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                localizer["Users_Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        return (true, result.Value.Data, result.Value.ContentType);
    }

    // GDPR Art. 9 data: owner-only here, and shown elsewhere only behind MedicalDataViewer.
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

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, logger);
}
