using Humans.Consent.Contracts;
using Humans.Users.Services;
using Humans.Base.Attributes;
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
    IOnboardingIntake onboardingService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<UsersResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer,
    IGovernanceService governanceService,
    IConsentService consentService,
    MedicalConditionsService medicalConditions) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

    [HttpGet("")]
    [AllowBeforeActiveMembership]
    public IActionResult Index() => RedirectToAction(nameof(Me));

    [HttpGet("Me")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var viewModel = await BuildOwnProfileAsync(ct);
        return viewModel is null ? NotFound() : View("Index", viewModel);
    }

    // Side-by-side trial of the identity-column layout; /Profile/Me stays the live page until Peter picks.
    [HttpGet("~/Users/Me")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> UsersMe(CancellationToken ct)
    {
        var viewModel = await BuildOwnProfileAsync(ct);
        return viewModel is null ? NotFound() : View("Me", viewModel);
    }

    private async Task<ProfileViewModel?> BuildOwnProfileAsync(CancellationToken ct)
    {
        var info = await GetCurrentUserInfoAsync(ct);
        if (info is null)
            return null;

        var profile = info.Profile;
        var pendingConsentCount = (await consentService.GetConsentProgressAsync(info.Id, ct)).PendingCount;

        var applications = await governanceService.GetUserApplicationsAsync(info.Id, ct);
        var latestApplication = applications.MaxBy(a => a.SubmittedAt);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = info.Id,
            HasPendingConsents = pendingConsentCount > 0,
            PendingConsentCount = pendingConsentCount,
            IsOwnProfile = true,
        };

        if (latestApplication is not null && latestApplication.Status != ApplicationStatus.Withdrawn)
        {
            viewModel.TierApplicationStatus = latestApplication.Status;
            viewModel.TierApplicationTier = latestApplication.MembershipTier;
            viewModel.TierApplicationBadgeClass = EnumBadgeMap.For(latestApplication.Status);
        }

        return viewModel;
    }

    [HttpGet("Me/Edit")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Edit([FromQuery] bool preview = false, CancellationToken ct = default)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info is null) return NotFound();

        var applications = await governanceService.GetUserApplicationsAsync(user.Id, ct);
        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            preview);

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(21 * 1024 * 1024)] // 20 MB picture plus multipart overhead; upload validation enforces the file cap.
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return NotFound();
        model.UserId = user.Id;
        model.HasCustomProfilePicture = user.Profile?.HasCustomPicture == true;
        model.ContactFields = ProfileEditViewModelBuilder.BuildLegacyContactFields(user.Profile);
        model.IsInitialSetup = user.NeedsProfileSetup;

        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var pictureUpload = await ReadProfilePictureUploadAsync(model);
        var saveResult = await SaveEditedProfileOrNullAsync(model, user.Id, pictureUpload);
        if (saveResult is not null)
            return saveResult;

        // ProfileEditorService must not call Onboarding back (no leaf-to-director callbacks), so the controller
        // reports the save.
        await onboardingService.ProfileSavedAsync(user.Id);

        SetSuccess(sharedLocalizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(Me));
    }

    // CV: existing rows keep Id and CreatedAt; new rows post Guid.Empty and get a fresh Id.
    private static ProfileSaveRequest BuildProfileSaveRequest(
        ProfileViewModel model, (byte[]? Data, string? ContentType, string? FileName) pictureUpload)
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
            VolunteerHistory: cvEntries,
            ProfilePictureFileName: pictureUpload.FileName);
    }

    private async Task<IActionResult?> SaveEditedProfileOrNullAsync(
        ProfileViewModel model, Guid userId, (byte[]? Data, string? ContentType, string? FileName) pictureUpload)
    {
        var saveRequest = BuildProfileSaveRequest(model, pictureUpload);

        var languages = model.EditableLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
            .Select(l => new ProfileLanguageInfo(Guid.NewGuid(), l.LanguageCode.Trim(), l.Proficiency))
            .ToList();
        var result = await profileEditorService.SaveProfileAsync(
            userId, saveRequest, contactFields: BuildContactFields(model), languages: languages,
            removedLegacyContactFieldIds: model.RemovedLegacyContactFieldIds);
        if (result.Succeeded) return null;
        var errorKey = result.ErrorKey;

        var field = result.ErrorField?.Replace("ContactFields[", "EditableContactFields[", StringComparison.Ordinal)
            ?? (string.Equals(errorKey, ProfileEditorService.BurnerCvRequiredErrorKey, StringComparison.Ordinal)
                ? nameof(model.NoPriorBurnExperience) : string.Empty);
        if (string.Equals(field, nameof(ProfileSaveRequest.ProfilePictureData), StringComparison.Ordinal))
            field = nameof(model.ProfilePictureUpload);
        var message = result.ErrorArgumentKey is { } labelKey && errorKey is not null
            ? sharedLocalizer[errorKey, localizer[labelKey].Value].Value
            : errorKey is not null ? localizer[errorKey].Value : sharedLocalizer["Common_Error"].Value;
        ModelState.AddModelError(field, message);
        model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
            && string.IsNullOrEmpty(model.LastName)
            && string.IsNullOrEmpty(model.EmergencyContactName);
        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(model);
    }

    private static IReadOnlyList<ContactFieldEditDto> BuildContactFields(ProfileViewModel model)
    {
        return model.EditableContactFields
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
    }

    private async Task<(byte[]? Data, string? ContentType, string? FileName)> ReadProfilePictureUploadAsync(ProfileViewModel model)
    {
        if (model.ProfilePictureUpload is not { Length: > 0 } upload)
            return (null, null, null);
        using var uploadStream = new MemoryStream();
        await upload.CopyToAsync(uploadStream, HttpContext.RequestAborted);
        return (uploadStream.ToArray(), upload.ContentType, upload.FileName);
    }

    // GDPR Art. 9 data: owner-only here; anyone else goes through ProfileViewController.RevealMedical.
    [HttpGet("Me/Medical")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Medical(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        return View(new MedicalViewModel
        {
            MedicalConditions = await medicalConditions.GetOwnAsync(user.Id, ct),
            UserId = user.Id,
        });
    }

    [HttpPost("Me/Medical")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Medical([Bind(nameof(MedicalViewModel.MedicalConditions))] MedicalViewModel model)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        try
        {
            // From the principal, so the reveal list on a re-rendered form is always the member's own.
            model.UserId = user.Id;
            if (!ModelState.IsValid)
                return View(model);

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
}
