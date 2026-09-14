using Humans.Users.Services;
using Humans.Base.Attributes;
using Humans.Base.Models.Tables;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
using Humans.Gdpr.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
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
[CrossSectionWrite("Profile submits and edits the member tier application.")]
internal sealed class ProfileController(
    IUserServiceInternal userService,
    UserManager<User> userManager,
    IProfileEditorService profileEditorService,
    IContactFieldService contactFieldService,
    ICommunicationPreferenceService commPrefService,
    IOnboardingIntake onboardingService,
    IShiftSignups shiftSignupService,
    IBurnSettingsService burnSettings,
    IShiftVolunteerProfiles shiftProfiles,
    IShiftView shiftView,
    IGdprService gdprExportService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<UsersResource> localizer,
    // The tier-application copy and the generic Common_/Validation_ strings this controller
    // formats stayed in SharedResource at the Users carve — Governance renders the same form.
    IStringLocalizer<SharedResource> sharedLocalizer,
    ICampaignServiceRead campaignService,
    IEmailOutboxServiceRead emailOutboxService,
    IClock clock,
    IApplicationDecisionService applicationDecisionService,
    IAccountDeletionService accountDeletionService,
    IMembershipCalculatorRead membershipCalculator) : HumansControllerBase(userService)
{
    private readonly IUserServiceInternal _userService = userService;

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

        var applications = await applicationDecisionService.GetUserApplicationsAsync(info.Id, ct);
        var latestApplication = applications.MaxBy(a => a.SubmittedAt);

        var campaignGrants = await campaignService.GetActiveOrCompletedGrantsForUserAsync(info.Id, ct);

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = info.Id,
            HasPendingConsents = pendingConsentCount > 0,
            PendingConsentCount = pendingConsentCount,
            IsApproved = profile?.IsApproved ?? false,
            IsOwnProfile = true,
            DisplayName = info.BurnerName,
            CampaignGrants = campaignGrants,
            // Onsite chip — own profile, always visible. Issue
            // nobodies-collective/Humans#736.
            OnsiteSince = await ResolveOnsiteSinceAsync(info),
            CanViewOnsiteChip = true,
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

    /// <summary>
    /// Returns the user's "onsite since" instant for the active event year, or
    /// null if they are not yet checked in (or there is no active event). Reads
    /// from the cached <see cref="UserInfo"/> snapshot — no extra DB hit. Issue
    /// nobodies-collective/Humans#736.
    /// </summary>
    private async Task<Instant?> ResolveOnsiteSinceAsync(UserInfo info)
    {
        var active = await burnSettings.GetActiveAsync();
        if (active is null || active.Year == 0) return null;
        return info.OnsiteSinceForYear(active.Year);
    }

    [HttpGet("Me/Edit")]
    public async Task<IActionResult> Edit([FromQuery] bool preview = false, CancellationToken ct = default)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info is null) return NotFound();

        var applications = await applicationDecisionService.GetUserApplicationsAsync(user.Id, ct);
        var allShiftTags = await shiftProfiles.GetTagsAsync();
        // Tag prefs from cached ShiftUserView, not repo.
        var userShiftView = await shiftView.GetUserAsync(user.Id, ct);
        var preferredShiftTags = userShiftView.TagPreferences;
        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            allShiftTags,
            preferredShiftTags,
            preview,
            p => Url.Action(nameof(ProfileViewController.Picture), "ProfileView", new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

        // Meal preference + allergies are Profile fields, surfaced on Edit as a
        // second entry point alongside the dedicated DietaryMedical page (which owns
        // intolerances + medical). Read straight off the UserInfo we already loaded.
        viewModel.DietaryPreference = info.Profile?.DietaryPreference ?? string.Empty;
        viewModel.Allergies = info.Profile is null ? [] : [.. info.Profile.Allergies];
        viewModel.AllergyOtherText = info.Profile?.AllergyOtherText;

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        // Tag catalog not posted back — repopulate up front so validation-failure rerenders the picker.
        model.AllShiftTags = (await shiftProfiles.GetTagsAsync())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        ValidateEditSimpleFieldGuards(model);
        if (ModelState.ErrorCount > 0)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Canonical predicate — UserInfo.IsApproved is the Consent-Coordinator gate, so
        // "not approved yet" IS initial setup (it already absorbs the no-profile case).
        // See memory/architecture/derived-predicates-on-userinfo.md.
        var isInitialSetup = !user.IsApproved;

        var burnerCvResult = ValidateBurnerCvOrNull(model, isInitialSetup);
        if (burnerCvResult is not null)
            return burnerCvResult;

        var tierValidationResult = ValidateTierApplicationOrNull(model, isInitialSetup);
        if (tierValidationResult is not null)
            return tierValidationResult;

        var pictureUpload = await TryReadProfilePictureUploadAsync(model);
        if (!pictureUpload.Success)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var (profileId, saveResult) = await SaveEditedProfileOrNullAsync(model, user.Id, isInitialSetup, pictureUpload);
        if (saveResult is not null)
            return saveResult;

        // Cross-section peer calls (consent-check trigger, tier-application
        // submit/update, pending-deletion cancel) stay controller-side per
        // no-leaf-to-director-callbacks — the controller is the director,
        // services are peers/leaves that must not call each other back.
        await RunPostProfileSaveOrchestrationAsync(user, model, isInitialSetup);

        var contactFieldsResult = await SaveEditedContactFieldsOrNullAsync(model, user.Id, profileId);
        if (contactFieldsResult is not null)
            return contactFieldsResult;

        // Languages: remove-and-replace.
        var newLanguages = model.EditableLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
            .Select(l => new ProfileLanguageInfo(
                Guid.NewGuid(),
                l.LanguageCode.Trim(),
                l.Proficiency))
            .ToList();

        await _userService.SaveProfileLanguagesAsync(profileId, newLanguages);

        await shiftProfiles.SetVolunteerTagPreferencesAsync(user.Id, model.EditableShiftTagIds);

        // Meal pref + allergies were persisted as part of the ProfileSaveRequest
        // above (Profile now owns dietary). Intolerances + medical are untouched —
        // they're owned by the DietaryMedical page.

        SetSuccess(sharedLocalizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(Me));
    }

    // Simple field-level guards (phone E.164 format, allergy "Other" text) —
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

        // Allergy "Other" requires accompanying free text (mirrors DietaryMedical POST).
        // Validated here, before any persistence, so a bad submit can't half-save
        // (main profile written but shift profile rejected, or vice versa).
        if (model.Allergies.Contains(DietaryOptions.OtherOption) && string.IsNullOrWhiteSpace(model.AllergyOtherText))
        {
            ModelState.AddModelError(nameof(model.AllergyOtherText),
                localizer["Profile_DietaryMedical_AllergyOther_Required"].Value);
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

    // Maps IApplicationDecisionService's tier-application field rules onto localized,
    // form-field-targeted ModelState errors — the same error keys, and the same mapping
    // shape, GovernanceApplicationsController uses on the submit path. The rules
    // themselves live in the service that owns tier-application state; this runs them
    // before any persistence so a bad submit can't half-save. Returns the rerender
    // result when invalid, null to continue.
    private IActionResult? ValidateTierApplicationOrNull(ProfileViewModel model, bool isInitialSetup)
    {
        if (!isInitialSetup || model.SelectedTier == MembershipTier.Volunteer)
            return null;

        var result = applicationDecisionService.ValidateSubmission(
            model.SelectedTier, model.ApplicationMotivation,
            model.ApplicationSignificantContribution, model.ApplicationRoleUnderstanding);

        if (result.Success)
            return null;

        switch (result.ErrorKey)
        {
            case "MotivationRequired":
                ModelState.AddModelError(nameof(model.ApplicationMotivation),
                    sharedLocalizer["Profile_MotivationRequired"].Value);
                break;
            case "SignificantContributionRequired":
                ModelState.AddModelError(nameof(model.ApplicationSignificantContribution),
                    sharedLocalizer["Application_SignificantContributionRequired"].Value);
                break;
            case "RoleUnderstandingRequired":
                ModelState.AddModelError(nameof(model.ApplicationRoleUnderstanding),
                    sharedLocalizer["Application_RoleUnderstandingRequired"].Value);
                break;
            default:
                ModelState.AddModelError(string.Empty, sharedLocalizer["Application_InvalidTier"].Value);
                break;
        }

        model.IsInitialSetup = true;
        model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
            && string.IsNullOrEmpty(model.LastName)
            && string.IsNullOrEmpty(model.EmergencyContactName);
        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(model);
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
            // Meal pref + allergies (the DietaryMedical page owns intolerances + medical).
            DietaryPreference: string.IsNullOrWhiteSpace(model.DietaryPreference) ? null : model.DietaryPreference,
            Allergies: [.. model.Allergies.Where(a => DietaryOptions.AllergyOptions.Contains(a, StringComparer.Ordinal))],
            AllergyOtherText: model.Allergies.Contains(DietaryOptions.OtherOption) ? model.AllergyOtherText?.Trim() : null,
            VolunteerHistory: cvEntries);
    }

    private async Task<(Guid ProfileId, IActionResult? EarlyReturn)> SaveEditedProfileOrNullAsync(
        ProfileViewModel model, Guid userId, bool isInitialSetup, (bool Success, byte[]? Data, string? ContentType) pictureUpload)
    {
        var saveRequest = BuildProfileSaveRequest(model, pictureUpload);

        try
        {
            var profileId = await profileEditorService.SaveProfileAsync(
                userId, model.BurnerName, saveRequest);
            return (profileId, null);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.IsInitialSetup = isInitialSetup;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return (Guid.Empty, View(model));
        }
    }

    private async Task RunPostProfileSaveOrchestrationAsync(UserInfo user, ProfileViewModel model, bool isInitialSetup)
    {
        // Peer-call into Onboarding; ProfileEditorService doesn't.
        await onboardingService.SetConsentCheckPendingIfEligibleAsync(user.Id);

        // Initial-setup tier-app: form's `isTierLocked` guard + ApplicationDecisionService AlreadyPending backstop.
        if (isInitialSetup && model.SelectedTier != MembershipTier.Volunteer)
        {
            var existingApps = await applicationDecisionService.GetUserApplicationsAsync(user.Id);
            var existingDraft = existingApps.FirstOrDefault(a =>
                a.Status == ApplicationStatus.Submitted);
            var hasApprovedApp = existingApps.Any(a =>
                a.Status == ApplicationStatus.Approved);

            if (existingDraft is not null)
            {
                await applicationDecisionService.UpdateDraftApplicationAsync(
                    existingDraft.Id,
                    model.SelectedTier,
                    model.ApplicationMotivation!,
                    model.ApplicationAdditionalInfo,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationSignificantContribution : null,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationRoleUnderstanding : null);
            }
            else if (!hasApprovedApp)
            {
                await applicationDecisionService.SubmitAsync(
                    user.Id, model.SelectedTier,
                    model.ApplicationMotivation!,
                    model.ApplicationAdditionalInfo,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationSignificantContribution : null,
                    model.SelectedTier == MembershipTier.Asociado ? model.ApplicationRoleUnderstanding : null,
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            }
        }

        // Route pending-deletion cancel through IAccountDeletionService, not raw UserManager.
        if (isInitialSetup && user.IsDeletionPending)
        {
            await accountDeletionService.CancelDeletionAsync(user.Id);
            logger.LogInformation(
                "Cancelled pending deletion request for user {UserId} on profile creation",
                user.Id);
        }
    }

    private async Task<IActionResult?> SaveEditedContactFieldsOrNullAsync(ProfileViewModel model, Guid userId, Guid profileId)
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

        try
        {
            await contactFieldService.SaveContactFieldsAsync(profileId, contactFieldDtos);
            return null;
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(ex, "Failed to save contact fields for user {UserId} and profile {ProfileId}", userId, profileId);
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }
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
        await upload.CopyToAsync(uploadStream);
        var result = ResizeProfilePicture(uploadStream.ToArray());
        if (result is null)
        {
            ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                localizer["Profile_PictureInvalidFormat"].Value);
            return (false, null, null);
        }

        return (true, result.Value.Data, result.Value.ContentType);
    }

    [HttpPost("Me/DeclareNotAttending")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclareNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return Redirect("/");
            }

            await _userService.DeclareNotAttendingAsync(user.Id, eventYear.Value);
            SetSuccess("You've been marked as not attending this year.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to declare not attending for user {UserId}", user.Id);
            SetError("Something went wrong. Please try again.");
        }

        return Redirect("/");
    }

    [HttpPost("Me/UndoNotAttending")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return Redirect("/");
            }

            var undone = await _userService.UndoNotAttendingAsync(user.Id, eventYear.Value);
            SetUndoNotAttendingResult(undone);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to undo not attending for user {UserId}", user.Id);
            SetError("Something went wrong. Please try again.");
        }

        return Redirect("/");
    }

    private async Task<int?> GetActiveEventYearOrSetErrorAsync()
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            return activeEvent.Year;
        }

        SetError("No active event configured.");
        return null;
    }

    private void SetUndoNotAttendingResult(bool undone)
    {
        if (undone)
        {
            SetSuccess("Your declaration has been removed.");
            return;
        }

        SetError("Could not undo — your status may have been updated by ticket sync.");
    }

    [HttpGet("Me/Outbox")]
    public async Task<IActionResult> MyOutbox()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var messages = await emailOutboxService.GetMessagesForUserAsync(user.Id);

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

        ViewData["DpoEmail"] = configuration.GetOptionalSetting(configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
        return View(viewModel);
    }

    [HttpPost("Me/Privacy/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await accountDeletionService.RequestDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                SetError(localizer["Profile_DeletionAlreadyPending"].Value);
            return RedirectToAction(nameof(Privacy));
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture,
            localizer["Profile_DeletionRequested"].Value,
            result.EffectiveDeletionDate?.ToDateTimeUtc().ToDate() ?? ""));
        return RedirectToAction(nameof(Privacy));
    }

    [HttpGet("Me/DietaryMedical")]
    public async Task<IActionResult> DietaryMedical(
        string? returnAction = null,
        Guid? shiftId = null,
        Guid? rotaId = null,
        int? startDayOffset = null,
        int? endDayOffset = null)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            // Dietary + medical are Profile fields now. The owner sees their own
            // medical here (allowed); the data comes from the cached UserInfo.
            var vm = user.Profile is null
                ? new DietaryMedicalViewModel()
                : DietaryMedicalViewModel.FromProfile(user.Profile);

            // Carryover from the dietary-gate redirect (ShiftsController.SignUp/SignUpRange).
            // The POST handler reads these to replay the original signup after a successful save.
            vm.ReturnAction = returnAction;
            vm.ShiftId = shiftId;
            vm.RotaId = rotaId;
            vm.StartDayOffset = startDayOffset;
            vm.EndDayOffset = endDayOffset;

            return View(vm);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load dietary/medical info for user");
            SetError(localizer["Profile_DietaryMedical_LoadFailed"].Value);
            return RedirectToAction(nameof(Me));
        }
    }

    [HttpPost("Me/DietaryMedical")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DietaryMedical(DietaryMedicalViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // Controller guards are the localized, field-targeted primary path for the
        // "Other text required iff Other selected" rule. SaveDietaryMedicalAsync
        // throws ValidationException as a backstop for non-controller callers.
        if (model.Allergies.Contains(DietaryMedicalViewModel.OtherOption) && string.IsNullOrWhiteSpace(model.AllergyOtherText))
        {
            ModelState.AddModelError(nameof(model.AllergyOtherText), localizer["Profile_DietaryMedical_AllergyOther_Required"].Value);
            return View(model);
        }
        if (model.Intolerances.Contains(DietaryMedicalViewModel.OtherOption) && string.IsNullOrWhiteSpace(model.IntoleranceOtherText))
        {
            ModelState.AddModelError(nameof(model.IntoleranceOtherText), localizer["Profile_DietaryMedical_IntoleranceOther_Required"].Value);
            return View(model);
        }

        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            // Owner editing their own dietary + medical — write the six Profile
            // columns (the editor leaves all other profile fields untouched).
            await profileEditorService.SaveDietaryMedicalAsync(user.Id, model.ToCommand());

            // Signup-replay — the user was bounced here from
            // ShiftsController.ToggleDay by the dietary gate. After a
            // successful save we re-run the original signup and land them on
            // /Shifts with the appropriate flash. See
            // src/Sections/Humans.Users/Docs/features/dietary-medical-nudge.md (US-35.6).
            // Replay failure does NOT roll back the dietary save — the user can
            // retry the signup directly from /Shifts without re-entering it.
            return await ReplayShiftSignupAfterDietaryMedicalSaveAsync(user.Id, model);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save dietary/medical info");
            SetError(localizer["Profile_DietaryMedical_SaveFailed"].Value);
            return View(model);
        }
    }

    // Flash mapping duplicated from ShiftsController.SignUp/SignUpRange; sharing it is tracked separately.
    private async Task<IActionResult> ReplayShiftSignupAfterDietaryMedicalSaveAsync(Guid userId, DietaryMedicalViewModel model)
    {
        switch (model.ReturnAction)
        {
            case "signup" when model.ShiftId is { } sid:
                {
                    var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                    var result = await shiftSignupService.SignUpAsync(
                        userId,
                        sid,
                        actorUserId: null,
                        flags: privileged ? ShiftSignupRequestFlags.Privileged : ShiftSignupRequestFlags.None);
                    if (!result.Success)
                        SetError(result.Error ?? "Shift signup failed.");
                    else
                        SetSuccess(result.Warning is not null
                            ? $"Signed up successfully. Note: {result.Warning}"
                            : "Signed up successfully!");
                    return RedirectToAction("Index", "Shifts");
                }
            case "signuprange" when model.RotaId is { } rid
                                     && model.StartDayOffset is { } sd
                                     && model.EndDayOffset is { } ed:
                {
                    var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                    var flags = ShiftSignupRequestFlags.SkipConflicts;
                    if (privileged) flags |= ShiftSignupRequestFlags.Privileged;
                    var result = await shiftSignupService.SignUpRangeAsync(
                        userId,
                        rid,
                        sd,
                        ed,
                        actorUserId: null,
                        flags: flags);
                    if (!result.Success)
                        SetError(result.Error ?? "Shift range signup failed.");
                    else
                        SetSuccess(result.Warning is not null
                            ? $"Signed up for date range. Note: {result.Warning}"
                            : "Signed up for date range!");
                    return RedirectToAction("Index", "Shifts");
                }
            case "shifts":
                SetSuccess(localizer["Profile_DietaryMedical_Saved"].Value);
                return RedirectToAction("Index", "Shifts");
            default:
                SetSuccess(localizer["Profile_DietaryMedical_Saved"].Value);
                return RedirectToAction("Index", "Home");
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

            return View(model: user.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load communication preferences");
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

            await commPrefService.UpdatePreferenceAsync(
                user.Id, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Profile");

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

    [HttpGet("Me/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

        var payload = BuildExportPayload(export);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, ExportJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"nobodies-profiles-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

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


    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, logger);
}
