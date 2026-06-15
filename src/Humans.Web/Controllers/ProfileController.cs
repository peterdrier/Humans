// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Humans.Application.Architecture;
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
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Models;
using Humans.Application.Services.Profiles;

// RoleAssignment nav props are [Obsolete]; service stitches them in memory. Nav-strip tracked in §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Authorize]
[Route("Profile")]
public class ProfileController(
    IUserService userService,
    UserManager<User> userManager,
    IProfilePictureService profilePictureService,
    IProfileEditorService profileEditorService,
    IContactFieldService contactFieldService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IUserEmailService userEmailService,
    ICommunicationPreferenceService commPrefService,
    IAuditLogService auditLogService,
    IOnboardingService onboardingService,
    IShiftSignupService shiftSignupService,
    IShiftManagementService shiftMgmt,
    IShiftView shiftView,
    IGdprExportService gdprExportService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<SharedResource> localizer,
    ITicketServiceRead ticketQueryService,
    ITeamServiceRead teamService,
    ICampaignService campaignService,
    ICampServiceRead campService,
    IEmailOutboxServiceRead emailOutboxService,
    IClock clock,
    IAuthorizationService authorizationService,
    IApplicationDecisionService applicationDecisionService,
    IAccountDeletionService accountDeletionService,
    IMembershipCalculatorRead membershipCalculator,
    SignInManager<User> signInManager,
    IOptions<GoogleWorkspaceOptions> googleWorkspaceOptions,
    IAuditViewerService auditViewerService) : HumansControllerBase(userService)
{
    private readonly ITicketServiceRead _ticketQueryService = ticketQueryService;
    private readonly IUserService _userService = userService;
    private readonly GoogleWorkspaceOptions _googleWorkspaceOptions = googleWorkspaceOptions.Value;
    private readonly IAuditViewerService _auditViewerService = auditViewerService;

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
            viewModel.TierApplicationBadgeClass = latestApplication.Status.GetBadgeClass();
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
        var active = await shiftMgmt.GetActiveAsync();
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
        var allShiftTags = await shiftMgmt.GetTagsAsync();
        // see #720 (T-09) — tag prefs from cached ShiftUserView, not repo.
        var userShiftView = await shiftView.GetUserAsync(user.Id, ct);
        var preferredShiftTags = userShiftView.TagPreferences
            .Select(p => new ShiftTagPreferenceSummary(p.ShiftTagId, p.ShiftTag?.Name ?? string.Empty))
            .ToList();
        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            allShiftTags,
            preferredShiftTags,
            preview,
            p => Url.Action(nameof(Picture), new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

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
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Validation invariants (allergy Other, Burner CV) and CV save live in IProfileEditorService as defense-in-depth backstops; the localized field-targeted form guards and the cross-section after-save orchestration (consent-check trigger, tier-application submit/update, pending-deletion cancel) remain controller-side per no-leaf-to-director-callbacks and user-profile-foundational.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        // Tag catalog not posted back — repopulate up front so validation-failure rerenders the picker.
        model.AllShiftTags = (await shiftMgmt.GetTagsAsync())
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

        var phoneTypes = new[] { ContactFieldType.Phone, ContactFieldType.WhatsApp };
        for (var i = 0; i < model.EditableContactFields.Count; i++)
        {
            var cf = model.EditableContactFields[i];
            if (!string.IsNullOrWhiteSpace(cf.Value) && phoneTypes.Contains(cf.FieldType) && !cf.Value.TrimStart().StartsWith("+", StringComparison.Ordinal))
            {
                ModelState.AddModelError($"EditableContactFields[{i}].Value",
                    localizer["Validation_PhoneE164", localizer["Profile_" + cf.FieldType].Value].Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(model.EmergencyContactPhone) && !model.EmergencyContactPhone.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.EmergencyContactPhone),
                localizer["Validation_PhoneE164", localizer["Profile_EmergencyContactPhone"].Value].Value);
        }

        // Allergy "Other" requires accompanying free text (mirrors DietaryMedical POST).
        // Validated here, before any persistence, so a bad submit can't half-save
        // (main profile written but shift profile rejected, or vice versa).
        if (model.Allergies.Contains(DietaryOptions.OtherOption) && string.IsNullOrWhiteSpace(model.AllergyOtherText))
        {
            ModelState.AddModelError(nameof(model.AllergyOtherText),
                localizer["Profile_DietaryMedical_AllergyOther_Required"].Value);
        }

        if (ModelState.ErrorCount > 0)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Burner CV: entries OR "no prior experience".
        var hasVolunteerHistory = model.EditableVolunteerHistory
            .Any(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue);
        if (!model.NoPriorBurnExperience && !hasVolunteerHistory)
        {
            ModelState.AddModelError(nameof(model.NoPriorBurnExperience),
                localizer["Profile_BurnerCVRequired"].Value);
            var existingProfile = (await _userService.GetUserInfoAsync(user.Id))?.Profile;
            model.IsInitialSetup = existingProfile is null || !existingProfile.IsApproved;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        var profileForSetupCheck = (await _userService.GetUserInfoAsync(user.Id))?.Profile;
        var isInitialSetup = profileForSetupCheck is null || !profileForSetupCheck.IsApproved;
        if (isInitialSetup)
        {
            if (model.SelectedTier != MembershipTier.Volunteer &&
                string.IsNullOrWhiteSpace(model.ApplicationMotivation))
            {
                ModelState.AddModelError(nameof(model.ApplicationMotivation),
                    localizer["Profile_MotivationRequired"].Value);
                model.IsInitialSetup = true;
                model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                    && string.IsNullOrEmpty(model.LastName)
                    && string.IsNullOrEmpty(model.EmergencyContactName);
                ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                return View(model);
            }

            if (model.SelectedTier == MembershipTier.Asociado)
            {
                if (string.IsNullOrWhiteSpace(model.ApplicationSignificantContribution))
                {
                    ModelState.AddModelError(nameof(model.ApplicationSignificantContribution),
                        localizer["Application_SignificantContributionRequired"].Value);
                }
                if (string.IsNullOrWhiteSpace(model.ApplicationRoleUnderstanding))
                {
                    ModelState.AddModelError(nameof(model.ApplicationRoleUnderstanding),
                        localizer["Application_RoleUnderstandingRequired"].Value);
                }
                if (!ModelState.IsValid)
                {
                    model.IsInitialSetup = true;
                    model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                        && string.IsNullOrEmpty(model.LastName)
                        && string.IsNullOrEmpty(model.EmergencyContactName);
                    ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
                    return View(model);
                }
            }
        }

        var pictureUpload = await TryReadProfilePictureUploadAsync(model);
        if (!pictureUpload.Success)
        {
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // CV: existing rows keep Id/CreatedAt; new rows post Guid.Empty and get fresh Id.
        var cvEntries = model.EditableVolunteerHistory
            .Where(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue)
            .Select(vh => new CVEntry(
                vh.Id ?? Guid.Empty,
                vh.ParsedDate!.Value,
                vh.EventName,
                vh.Description
            ))
            .ToList();

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
            RemoveProfilePicture: model.RemoveProfilePicture,
            // Meal pref + allergies (the DietaryMedical page owns intolerances + medical).
            DietaryPreference: string.IsNullOrWhiteSpace(model.DietaryPreference) ? null : model.DietaryPreference,
            Allergies: [.. model.Allergies.Where(a => DietaryOptions.AllergyOptions.Contains(a, StringComparer.Ordinal))],
            AllergyOtherText: model.Allergies.Contains(DietaryOptions.OtherOption) ? model.AllergyOtherText?.Trim() : null,
            VolunteerHistory: cvEntries);

        Guid profileId;
        try
        {
            profileId = await profileEditorService.SaveProfileAsync(
                user.Id, model.BurnerName, saveRequest);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.IsInitialSetup = isInitialSetup;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Peer-call into Onboarding; ProfileEditorService doesn't.
        await onboardingService.SetConsentCheckPendingIfEligibleAsync(user.Id);

        // Initial-setup tier-app: form's `isTierLocked` guard + ApplicationDecisionService AlreadyPending backstop. see #685.
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
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(ex, "Failed to save contact fields for user {UserId} and profile {ProfileId}", user.Id, profileId);
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
            return View(model);
        }

        // Languages: remove-and-replace.
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

        await _userService.SaveProfileLanguagesAsync(profileId, newLanguages);

        await shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, model.EditableShiftTagIds);

        // Meal pref + allergies were persisted as part of the ProfileSaveRequest
        // above (Profile now owns dietary). Intolerances + medical are untouched —
        // they're owned by the DietaryMedical page.

        SetSuccess(localizer["Profile_Updated"].Value);
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

    [HttpGet("Me/Emails")]
    public async Task<IActionResult> Emails()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(user);
        return View(viewModel);
    }

    [HttpPost("Me/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.NewEmail) || !ModelState.IsValid)
        {
            if (string.IsNullOrWhiteSpace(model.NewEmail))
                ModelState.AddModelError(nameof(model.NewEmail), localizer["Profile_EnterEmail"].Value);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        try
        {
            var result = await userEmailService.AddEmailAsync(user.Id, model.NewEmail);
            await SendAddedEmailVerificationAsync(user, model.NewEmail, result);
            SetAddedEmailFlash(model.NewEmail, result.IsConflict);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(
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

        var info = await _userService.GetUserInfoAsync(user.Id);

        await emailService.SendAsync(emailMessages.EmailVerification(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            user.PreferredLanguage));

        logger.LogInformation(
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

        SetSuccess(string.Format(CultureInfo.CurrentCulture, localizer["Profile_VerificationSent"].Value, email.Trim()));
    }

    [HttpGet("Me/Emails/Verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, Guid emailId, string token)
    {
        if (string.IsNullOrEmpty(token) || emailId == Guid.Empty)
        {
            return VerifyEmailError(localizer["Profile_InvalidVerificationLink"].Value);
        }

        try
        {
            var decodedToken = HttpUtility.UrlDecode(token);
            var result = await userEmailService.VerifyEmailAsync(userId, emailId, decodedToken);

            return VerifyEmailSuccess(userId, result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("Email verification failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(localizer["Profile_InvalidVerificationLink"].Value);
        }
        catch (ValidationException ex)
        {
            logger.LogInformation("Email verification validation failed for user {UserId}: {Message}", userId, ex.Message);
            return VerifyEmailError(ex.Message);
        }
    }

    private IActionResult VerifyEmailSuccess(Guid userId, VerifyEmailResult result)
    {
        if (result.MergeRequestCreated)
        {
            logger.LogInformation(
                "User {UserId} verified email {Email} - merge request created",
                userId, result.Email);

            ViewData["Success"] = true;
            ViewData["Message"] = $"Email verified. A merge request has been submitted for admin review. The email {result.Email} will be added to your account once approved.";
            return View("VerifyEmailResult");
        }

        logger.LogInformation(
            "User {UserId} verified email {Email}",
            userId, result.Email);

        ViewData["Success"] = true;
        ViewData["Message"] = string.Format(localizer["Profile_EmailVerified"].Value, result.Email);
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

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            await userEmailService.SetPrimaryAsync(user.Id, emailId, ct);
            // Self audit at controller — SetPrimaryAsync doesn't take actorUserId.
            await auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), user.Id,
                $"Set primary email row {emailId}",
                user.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set primary email {EmailId} for user {UserId}", emailId, user.Id);
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

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var parsedVisibility = ParseEmailVisibility(visibility);

        try
        {
            await userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
            await LogSelfEmailVisibilityChangedAsync(user.Id, emailId, parsedVisibility);
            SetSuccess(localizer["Profile_EmailVisibilityUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set email visibility for email {EmailId} and user {UserId}", emailId, user.Id);
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
        auditLogService.LogAsync(
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

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var deleted = await userEmailService.DeleteEmailAsync(user.Id, emailId);
            if (deleted)
            {
                await LogSelfEmailDeletedAsync(user.Id, emailId);
                SetSuccess(localizer["Profile_EmailDeleted"].Value);
            }
            else
            {
                SetError(localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to delete email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private Task LogSelfEmailDeletedAsync(Guid userId, Guid emailId) =>
        auditLogService.LogAsync(
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

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.SetGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to set Google service email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_GoogleServiceUpdated"].Value);
            return;
        }

        SetError(localizer["EmailGrid_SetGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearGoogle(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearGoogleAsync(user.Id, emailId, user.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to clear Google flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetGoogleEmailClearedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_GoogleFlagCleared"].Value);
            return;
        }

        SetError(localizer["EmailGrid_ClearGoogleRejected"].Value);
    }

    [HttpPost("Me/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPrimary(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearPrimaryAsync(user.Id, emailId, user.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to clear primary flag on email {EmailId} for user {UserId}", emailId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetPrimaryEmailClearedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_PrimaryFlagCleared"].Value);
            return;
        }

        SetError(localizer["EmailGrid_ClearPrimaryRejected"].Value);
    }

    [HttpPost("Me/Emails/Link/{provider}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Link(string provider, string? returnUrl = null)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Round-trip via ExternalLoginCallback so link-while-signed-in branch fires.
        var resolvedReturnUrl = returnUrl ?? Url.Action(nameof(Emails)) ?? "/Profile/Me/Emails";
        var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl = resolvedReturnUrl })
            ?? "/Account/ExternalLoginCallback";
        var props = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(props, provider);
    }

    [HttpPost("Me/Emails/Unlink/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        try
        {
            var ok = await userEmailService.UnlinkAsync(user.Id, id, user.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to unlink email {EmailId} for user {UserId}", id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }

    private void SetEmailUnlinkedResult(bool ok)
    {
        if (ok)
        {
            SetSuccess(localizer["EmailGrid_UnlinkSuccess"].Value);
            return;
        }

        SetError(localizer["EmailGrid_UnlinkRejected"].Value);
    }

    // User-facing Unlink (see nobodies-collective/Humans#731) — keyed by (Provider, ProviderKey); enforces auth-method invariant server-side.
    [HttpPost("Me/LinkedAccounts/Unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkLinkedAccount(string provider, string providerKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerKey))
        {
            SetError(localizer["EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound();

        var authz = await authorizationService.AuthorizeAsync(User, user.Id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        // Stale dashboard or forged request — fail soft.
        var logins = await userManager.GetLoginsAsync(user);
        var hasLogin = logins.Any(l =>
            string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
            && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));
        if (!hasLogin)
        {
            SetError(localizer["EmailGrid_UnlinkRejected"].Value);
            return RedirectToAction(nameof(Emails));
        }

        // Route via UnlinkAsync to keep AspNetUserLogins + user_emails in sync. Orphan logins fall back to RemoveLoginAsync.
        // The last-verified-sign-in-method invariant is enforced inside UnlinkAsync (ValidationException).
        var rawRows = await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        var matching = rawRows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal)
            && string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        try
        {
            if (matching is not null)
            {
                var ok = await userEmailService.UnlinkAsync(user.Id, matching.Id, user.Id, ct);
                SetEmailUnlinkedResult(ok);
            }
            else
            {
                // Orphan login: no UserEmail row — drop directly. UnlinkAsync's guard can't
                // see this branch, so enforce the auth-method invariant here: with zero
                // verified emails this login may be the user's only sign-in path.
                if (rawRows.Count(r => r.IsVerified) < 1)
                {
                    SetError(localizer["LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value);
                    return RedirectToAction(nameof(Emails));
                }

                var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
                if (removeLogin.Succeeded)
                {
                    await auditLogService.LogAsync(
                        AuditAction.UserEmailUnlinked,
                        nameof(User), user.Id,
                        $"Unlinked orphan {provider} login (no matching UserEmail row)",
                        user.Id);
                    SetSuccess(localizer["EmailGrid_UnlinkSuccess"].Value);
                }
                else
                {
                    logger.LogWarning(
                        "UnlinkLinkedAccount: RemoveLoginAsync failed for user {UserId} provider {Provider}: {Errors}",
                        user.Id, provider,
                        string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
                    SetError(localizer["EmailGrid_UnlinkRejected"].Value);
                }
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
                "Failed to unlink provider {Provider} for user {UserId}: {Reason}",
                provider, user.Id, ex.Message);
            // UnlinkAsync's auth-method guard is the only ValidationException source here —
            // surface it via the localized string instead of the English exception message.
            SetError(ex is ValidationException
                ? localizer["LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value
                : ex.Message);
        }

        return RedirectToAction(nameof(Emails));
    }
    // Admin grid mirrors self-grid against a target user. No AdminLink: OAuth linking requires target's authentication.

    [HttpGet("{id:guid}/Admin/Emails")]
    public async Task<IActionResult> AdminEmails(Guid id, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        var viewModel = await BuildEmailsViewModelAsync(targetUser, isAdminContext: true, ct);
        return View("Emails", viewModel);
    }

    [HttpPost("{id:guid}/Admin/Emails/SetGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.SetGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set Google service email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/SetPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            await userEmailService.SetPrimaryAsync(id, emailId, ct);
            // Audit at controller — SetPrimaryAsync has no actorUserId.
            await auditLogService.LogAsync(
                AuditAction.UserEmailPrimarySet,
                nameof(User), id,
                $"Admin set primary email row {emailId}",
                actor.Id,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));
            SetSuccess(localizer["Profile_NotificationTargetUpdated"].Value);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set primary email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearGoogle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearGoogle(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearGoogleAsync(id, emailId, actor.Id, ct);
            SetGoogleEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to clear Google flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/ClearPrimary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminClearPrimary(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.ClearPrimaryAsync(id, emailId, actor.Id, ct);
            SetPrimaryEmailClearedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to clear primary flag on email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddEmail(Guid id, string email, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(localizer["Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var targetUser = await userManager.FindByIdAsync(id.ToString());
        if (targetUser is null)
            return NotFound();

        try
        {
            var result = await userEmailService.AddEmailAsync(id, email, ct);
            await SendAdminAddedEmailVerificationAsync(id, targetUser, email, result, actor.Id, ct);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
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

        var info = await _userService.GetUserInfoAsync(userId, ct);

        await emailService.SendAsync(emailMessages.EmailVerification(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            targetUser.PreferredLanguage),
            ct);

        logger.LogInformation(
            "Admin sent email verification to {Email} for user {UserId} (conflict: {IsConflict})",
            trimmedEmail, userId, result.IsConflict);

        await auditLogService.LogAsync(
            AuditAction.UserEmailAdded,
            nameof(User), userId,
            $"Admin added pending email {trimmedEmail} for user {userId} (conflict: {result.IsConflict})",
            actorId);

        SetSuccess(localizer["EmailGrid_AdminAddSentVerification"].Value);
    }
    // Admin recovery: insert a verified UserEmail without verification email.
    [HttpPost("{id:guid}/Admin/Emails/AddVerified")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminAddVerifiedEmail(Guid id, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            SetError(localizer["Profile_EnterEmail"].Value);
            return RedirectToAction(nameof(AdminEmails), new { id });
        }

        var actor = await userManager.GetUserAsync(User);
        var targetUser = await userManager.FindByIdAsync(id.ToString());

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
            var inserted = await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
            await ReportVerifiedEmailAddAsync(inserted, userId, email, actor.Id);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
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


        await auditLogService.LogAsync(
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
            var result = await userEmailService.AdminMarkVerifiedAsync(id, emailId, actor.Id, ct);
            if (result.MergeRequestCreated)
            {
                SetSuccess(localizer["EmailGrid_AdminVerifyMergeRequested"].Value);
            }
            else
            {
                SetSuccess(localizer["EmailGrid_AdminVerifySuccess"].Value);
            }
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(
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
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var ok = await userEmailService.UnlinkAsync(id, emailId, actor.Id, ct);
            SetEmailUnlinkedResult(ok);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to unlink email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    [HttpPost("{id:guid}/Admin/Emails/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminDeleteEmail(Guid id, Guid emailId, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            var deleted = await userEmailService.DeleteEmailAsync(id, emailId, ct);
            await SetAdminEmailDeletedResultAsync(id, emailId, actor.Id, deleted);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to delete email {EmailId} for user {UserId}", emailId, id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(AdminEmails), new { id });
    }

    private async Task SetAdminEmailDeletedResultAsync(Guid userId, Guid emailId, Guid actorId, bool deleted)
    {
        if (!deleted)
        {
            SetError(localizer["EmailGrid_DeleteRejectedHasProvider"].Value);
            return;
        }

        await auditLogService.LogAsync(
            AuditAction.UserEmailDeleted,
            nameof(User), userId,
            $"Admin deleted email row {emailId}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(localizer["Profile_EmailDeleted"].Value);
    }
    [HttpPost("{id:guid}/Admin/Emails/SetVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSetVisibility(
        Guid id, Guid emailId, ContactFieldVisibility? visibility, CancellationToken ct)
    {
        var authz = await authorizationService.AuthorizeAsync(User, id, UserEmailOperations.Edit);
        if (!authz.Succeeded)
            return Forbid();

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        try
        {
            await userEmailService.SetVisibilityAsync(id, emailId, visibility, ct);
            await SetAdminEmailVisibilityChangedResultAsync(id, emailId, actor.Id, visibility);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Admin failed to set email visibility for email {EmailId} and user {UserId}", emailId, id);
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
        await auditLogService.LogAsync(
            AuditAction.UserEmailVisibilityChanged,
            nameof(User), userId,
            $"Admin changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}",
            actorId,
            relatedEntityId: emailId,
            relatedEntityType: nameof(UserEmail));
        SetSuccess(localizer["Profile_EmailVisibilityUpdated"].Value);
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

    // CancelDeletion moved to UserController (Profile* retirement) — the cancel-deletion lever now
    // lives with the User section; Views/Profile/Privacy.cshtml posts to User/Deletion/Cancel.

    [HttpGet("Me/ShiftInfo")]
    public async Task<IActionResult> ShiftInfo()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();
            var profile = await shiftMgmt.GetShiftProfileAsync(user.Id);
            return View(ShiftInfoViewModel.FromProfile(profile));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load shift info for user");
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

            var shiftProfile = await shiftMgmt.GetOrCreateShiftProfileAsync(user.Id);

            shiftProfile.Skills = ShiftInfoViewModel.MergeSkills(
                model.SelectedSkills, model.SkillOtherText, shiftProfile.Skills);
            shiftProfile.Quirks = ShiftInfoViewModel.MergePersistedQuirks(
                model.TimePreference, model.SelectedQuirks, shiftProfile.Quirks);
            shiftProfile.Languages = ShiftInfoViewModel.MergeLanguages(
                model.SelectedLanguages, model.LanguageOtherText, shiftProfile.Languages);

            await shiftMgmt.UpdateShiftProfileAsync(shiftProfile);

            SetSuccess(localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(ShiftInfo));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save shift info for user");
            SetError("Failed to save shift info.");
            return View(model);
        }
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
    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 36 statements, cc 21.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
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

            // Signup-replay branches — the user was bounced here from
            // ShiftsController.SignUp/SignUpRange by the dietary gate. After a
            // successful save we re-run the original signup and land them on
            // /Shifts with the appropriate flash. See
            // docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md.
            // Replay failure does NOT roll back the dietary save — the user can
            // retry the signup directly from /Shifts without re-entering it.
            // The inline flash-mapping duplicates ShiftsController's two existing
            // inline copies; extract on the third call site per project doctrine.
            switch (model.ReturnAction)
            {
                case "signup" when model.ShiftId is { } sid:
                    {
                        var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                        var result = await shiftSignupService.SignUpAsync(
                            user.Id,
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
                            user.Id,
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

    // ─── Shared (Profile Picture) ────────────────────────────────────

    [HttpGet("Picture")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id, CancellationToken ct)
    {
        // §2: controller routes through the profile-picture service (owns FS read path + GDPR gate, see #527).
        var result = await profilePictureService.GetProfilePictureAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        return File(result.Value.Data, result.Value.ContentType);
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

        // Onsite chip visibility (#736): self always, plus the same admin/board
        // policy that gates /Tickets/Admin/Onsite. Coordinators below board
        // tier don't see the chip on other humans — wider visibility is a
        // follow-up PR if needed.
        var canViewOnsiteChip = isOwnProfile
            || (await authorizationService.AuthorizeAsync(
                User, PolicyNames.TicketAdminBoardOrAdmin)).Succeeded;

        var sentMessagesContext = await BuildSentMessagesContextAsync(id, viewer.Id, isOwnProfile, ct);

        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profileInfo.BurnerName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowContext.History,
            CanViewShiftSignups = noShowContext.CanView,
            OnsiteSince = canViewOnsiteChip
                ? await ResolveOnsiteSinceAsync(profileInfo)
                : null,
            CanViewOnsiteChip = canViewOnsiteChip,
            CanViewSentMessages = sentMessagesContext.CanView,
            SentMessages = sentMessagesContext.Messages,
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

        var viewerIsCoordinator = (await shiftMgmt.GetCoordinatorTeamIdsAsync(viewerId)).Count > 0;
        var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        if (!viewerCanViewShiftHistory)
        {
            return (false, null);
        }

        var noShows = await shiftSignupService.GetNoShowHistoryAsync(profileUserId);
        if (noShows.Count == 0)
        {
            return (true, null);
        }

        var noShowTeamIds = noShows.Select(s => s.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetTeamsAsync(ct);
        var noShowTeamNames = noShowTeamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        var reviewerIds = noShows
            .Where(s => s.ReviewedByUserId.HasValue)
            .Select(s => s.ReviewedByUserId!.Value)
            .Distinct()
            .ToList();
        var reviewers = reviewerIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
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
                ShiftDateLabel = zoned.ToDateTimeUnspecified().ToMonthDayTime(),
                MarkedByName = reviewer?.BurnerName,
                MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDateTimeUnspecified().ToMonthDayTime()
            };
        }).ToList());
    }

    /// <summary>
    /// Loads in-platform messages sent to the profile user, gated on the viewer being a
    /// coordinator or holding a privileged shift-management role. Uses the same coordinator
    /// check as <see cref="BuildNoShowHistoryContextAsync"/> so the two panels appear
    /// under consistent access rules.
    /// Returns <c>(false, null)</c> for own-profile views and non-coordinators.
    /// </summary>
    private async Task<(bool CanView, IReadOnlyList<Application.Services.AuditLog.AuditEvent>? Messages)>
        BuildSentMessagesContextAsync(Guid profileUserId, Guid viewerId, bool isOwnProfile, CancellationToken ct)
    {
        if (isOwnProfile)
            return (false, null);

        var viewerIsCoordinator = (await shiftMgmt.GetCoordinatorTeamIdsAsync(viewerId)).Count > 0;
        var isPrivilegedApprover = (await authorizationService.AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)).Succeeded;
        if (!viewerIsCoordinator && !isPrivilegedApprover)
            return (false, null);

        var messages = await _auditViewerService.GetFilteredAsync(
            entityType: nameof(User),
            entityId: profileUserId,
            userId: null,
            actions: [AuditAction.FacilitatedMessageSent],
            limit: 50,
            ct: ct);

        return (true, messages);
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

        var memberships = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType != SystemTeamType.Volunteers)
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == id) })
            .Where(x => x.Membership is not null)
            .Select(x => new TeamMembership(x.TeamInfo.Name, x.Membership!.Role) { IsHidden = x.TeamInfo.IsHidden })
            .ToList();
        // Camp + roles for the active season; rendered admin-only in the view.
        var camp = await campService.GetCampUserInfoAsync(id, ct);
        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships, camp);

        return PartialView("_HumanPopover", vm);
    }

    // Reduced popover served to anonymous viewers on public team pages (#771).
    // Mirrors the AllowAnonymous Profile/Picture endpoint pattern: only renders
    // when the target user is an active coordinator on a team that publishes
    // its coordinators (IsPublicPage && ShowCoordinatorsOnPublicPage). Returns
    // 404 otherwise so anonymous probes can't enumerate users. Filtering on the
    // controller per peters-hard-rules.md ("controllers ... responsible for
    // formatting, sorting, filtering") and to avoid expanding ITeamServiceRead
    // surface (memory/architecture/interface-method-additions-are-debt.md);
    // mirrors the inline TeamInfo filter the authenticated Popover uses above.
    [AllowAnonymous]
    [HttpGet("{id:guid}/PublicPopover")]
    public async Task<IActionResult> PublicPopover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null) return NotFound();

        var roleLabels = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive
                && !t.IsHidden
                && t.IsPublicPage
                && t.ShowCoordinatorsOnPublicPage
                && t.Members.Any(m => m.UserId == id && m.Role == TeamMemberRole.Coordinator))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"Coordinator · {t.Name}")
            .ToList();

        if (roleLabels.Count == 0) return NotFound();

        var vm = new PublicPopoverViewModel
        {
            UserId = info.Id,
            DisplayName = info.BurnerName,
            RoleLabels = roleLabels
        };

        return PartialView("_HumanPopoverPublic", vm);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        var targetInfo = await _userService.GetUserInfoAsync(id);
        if (targetInfo is null)
            return NotFound();

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetInfo.BurnerName,
            SenderEmail = currentUser.Email ?? string.Empty
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

        // see #635 (§15i) — bulk-fetch via section service, not cross-domain nav.
        var participants = await _userService.GetUserInfosAsync([id, currentUser.Id]);
        if (!participants.TryGetValue(id, out var targetUser))
            return NotFound();

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.BurnerName;
        model.SenderEmail = currentUser.Email ?? string.Empty;

        if (!ModelState.IsValid)
            return View(model);

        if (!participants.TryGetValue(currentUser.Id, out var sender))
            return NotFound();

        var request = FacilitatedMessageRequestBuilder.TryBuild(sender, targetUser, model);
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, localizer["Common_Error"].Value);
            return View(model);
        }

        await emailService.SendAsync(emailMessages.FacilitatedMessage(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.CleanMessage,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage));

        await auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            nameof(User), targetUser.Id,
            $"Message sent to {targetUser.BurnerName} (contact info shared: {(model.IncludeContactInfo ? "yes" : "no")})",
            currentUser.Id);

        SetSuccess(string.Format(
            localizer["SendMessage_Success"].Value,
            targetUser.BurnerName));

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

        // PublicAll = name + bio + public ContactFields. Admin bit gated by code review.
        // Uncapped: return the full match set so relevance ranking surfaces the right person
        // (a hard cap returned an arbitrary subset before sorting). Cheap at ~500 users.
        var results = await _userService.SearchUsersAsync(
            q, PersonSearchFields.PublicAll, limit: int.MaxValue, ct);

        // Display sort at controller — memory/architecture/display-sort-in-controllers.md.
        viewModel.Results = results
            .OrderByRelevance()
            .Select(r => r.ToHumanSearchViewModel())
            .ToList();

        return View(viewModel);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, logger);

    [Grandfathered(
        ruleId: "HUM0031",
        justification: "Worst-offender at HUM0031 introduction: 46 statements, cc 27.",
        since: "2026-06-09",
        issueRef: "nobodies-collective/Humans#857")]
    private async Task<EmailsViewModel> BuildEmailsViewModelAsync(User user, bool isAdminContext = false, CancellationToken ct = default)
    {
        var emails = await userEmailService.GetUserEmailsAsync(user.Id, ct);
        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        var burnerName = info?.BurnerName ?? string.Empty;

        var canAdd = true;
        var minutesUntilResend = 0;

        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail is not null)
        {
            var (cooldownCanAdd, cooldownMinutes, _) =
                await userEmailService.GetEmailCooldownInfoAsync(pendingEmail.Id, ct);
            canAdd = cooldownCanAdd;
            minutesUntilResend = cooldownMinutes;
        }

        var hasNobodiesTeam = emails.Any(e => e.IsVerified &&
            e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        // Use the already-loaded `emails` — UserManager doesn't .Include(UserEmails).
        var googleServiceEmail = emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();

        // Workspace canonical: Provider=Google + Workspace-domain email. Locks Primary + Google radios.
        var workspaceDomainSuffix = "@" + _googleWorkspaceOptions.Domain;
        var workspaceCandidates = emails
            .Where(e => !string.IsNullOrEmpty(e.Provider)
                && string.Equals(e.Provider, "Google", StringComparison.OrdinalIgnoreCase)
                && e.Email.EndsWith(workspaceDomainSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var workspaceLockedEmail = workspaceCandidates.FirstOrDefault(e => e.IsPrimary)
            ?? workspaceCandidates.FirstOrDefault();

        // see nobodies-collective/Humans#697 — admin diagnostic loads AspNetUserLogins + computes store-disagreement.
        IReadOnlyList<(string Provider, string ProviderKey)> userLogins = [];
        IReadOnlyList<UserEmailRowSnapshot> rawUserEmails = [];
        if (isAdminContext)
        {
            var loginsByUser = await _userService.GetExternalLoginsByUserIdsAsync([user.Id], ct);
            if (loginsByUser.TryGetValue(user.Id, out var list))
                userLogins = list;
            rawUserEmails = await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        }

        // see nobodies-collective/Humans#731 — self uses UserManager (ProviderDisplayName); stitches UserEmail row id + CreatedAt.
        IReadOnlyList<LinkedOAuthAccountViewModel> linkedAccounts = [];
        if (!isAdminContext)
        {
            var logins = await userManager.GetLoginsAsync(user);
            if (logins.Count > 0)
            {
                // (Provider, ProviderKey) uniqueness is service-enforced, not DB-enforced — keep first row per key.
                var rowsByKey = new Dictionary<(string, string), UserEmailRowSnapshot>();
                foreach (var r in await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct))
                {
                    if (string.IsNullOrEmpty(r.Provider) || string.IsNullOrEmpty(r.ProviderKey))
                        continue;
                    rowsByKey.TryAdd((r.Provider!, r.ProviderKey!), r);
                }

                // Auth-method invariant: at least one verified row must remain post-unlink (orphan logins don't touch rows).
                var verifiedTotal = emails.Count(e => e.IsVerified);

                linkedAccounts = logins.Select(l =>
                {
                    rowsByKey.TryGetValue((l.LoginProvider, l.ProviderKey), out var row);
                    var rowIsVerified = row?.IsVerified == true;
                    var verifiedAfter = verifiedTotal - (rowIsVerified ? 1 : 0);
                    return new LinkedOAuthAccountViewModel
                    {
                        Provider = l.LoginProvider,
                        ProviderKey = l.ProviderKey,
                        ProviderDisplayName = l.ProviderDisplayName,
                        ProviderKeyHash = HashForDisplay(l.ProviderKey),
                        MatchingUserEmailId = row?.Id,
                        Email = row?.Email,
                        LinkedAt = row?.CreatedAt,
                        CanUnlink = verifiedAfter >= 1,
                    };
                }).ToList();
            }
        }

        // see nobodies-collective/Humans#758 — addresses linked to the user's event ticket.
        // The grid hides Delete for these rows; UserEmailService.DeleteEmailAsync re-validates.
        var ticketOrders = await _ticketQueryService.GetTicketOrdersAsync(ct);
        var ticketEmails = ticketOrders
            .Where(o => o.MatchedUserId == user.Id && !string.IsNullOrWhiteSpace(o.BuyerEmail))
            .Select(o => o.BuyerEmail!)
            .Concat(ticketOrders
                .SelectMany(o => o.Attendees)
                .Where(a => a.MatchedUserId == user.Id && !string.IsNullOrWhiteSpace(a.AttendeeEmail))
                .Select(a => a.AttendeeEmail!))
            .ToList();

        bool RowIsTicketLinked(string address) =>
            ticketEmails.Any(ticketEmail => Domain.Helpers.EmailNormalization.EmailsMatch(address, ticketEmail));

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
                GoogleEmailStatus = e.GoogleEmailStatus,
                IsPrimary = e.IsPrimary,
                Visibility = e.Visibility,
                IsPendingVerification = e.IsPendingVerification,
                IsMergePending = e.IsMergePending,
                IsNobodiesTeamDomain = e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase),
                Provider = e.Provider,
                HasOrphanProviderTag = RowHasOrphanProviderTag(e.Provider, e.ProviderKey),
                IsTicketLinked = RowIsTicketLinked(e.Email),
            }).ToList(),
            ExternalLogins = userLogins.Select(l => new ExternalLoginRowViewModel
            {
                LoginProvider = l.Provider,
                ProviderKeyHash = HashForDisplay(l.ProviderKey),
                ProviderDisplayName = null,
                HasOrphanLogin = LoginHasOrphanRow(l.Provider, l.ProviderKey),
            }).ToList(),
            RawUserEmails = rawUserEmails,
            LinkedAccounts = linkedAccounts,
            CanAddEmail = canAdd,
            MinutesUntilResend = minutesUntilResend,
            GoogleServiceEmail = googleServiceEmail,
            HasNobodiesTeamEmail = hasNobodiesTeam,
            GoogleEmailStatus = user.GoogleEmailStatus,
            TargetUserId = user.Id,
            TargetDisplayName = burnerName,
            IsAdminContext = isAdminContext,
            WorkspaceLockedEmailId = workspaceLockedEmail?.Id,
            LegacyIdentityEmailColumn = isAdminContext
                && User.IsInRole(RoleNames.Admin)
                ? user.IdentityEmailColumn
                : null,
            TargetUserInfo = isAdminContext
                && User.IsInRole(RoleNames.Admin)
                    ? info
                    : null,
        };
    }

}
