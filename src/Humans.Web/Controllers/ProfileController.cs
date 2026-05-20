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

// RoleAssignment nav props are [Obsolete]; service stitches them in memory. Nav-strip tracked in §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Authorize]
[Route("Profile")]
public class ProfileController(
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
    IShiftView shiftView,
    IGdprExportService gdprExportService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    ILogger<ProfileController> logger,
    IStringLocalizer<SharedResource> localizer,
    ITicketQueryService ticketQueryService,
    ITeamService teamService,
    ICampaignService campaignService,
    IEmailOutboxService emailOutboxService,
    IClock clock,
    IAuthorizationService authorizationService,
    IConsentService consentService,
    IApplicationDecisionService applicationDecisionService,
    IAccountDeletionService accountDeletionService,
    IMembershipCalculator membershipCalculator,
    IHttpClientFactory httpClientFactory,
    SignInManager<User> signInManager,
    IOptions<GoogleWorkspaceOptions> googleWorkspaceOptions) : HumansControllerBase(userService)
{
    private readonly ITicketQueryService _ticketQueryService = ticketQueryService;
    private readonly IUserService _userService = userService;
    private readonly GoogleWorkspaceOptions _googleWorkspaceOptions = googleWorkspaceOptions.Value;

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

    // Admin humans list: bit-flag PersonSearchFields filter + status partition + projection.
    private async Task<IReadOnlyList<AdminHumanRow>> BuildAdminHumansAsync(
        string? search, string? statusFilter, CancellationToken ct)
    {
        var allUsers = await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = allUsers.Select(u => u.Id).ToList();
        var notificationEmails =
            await userEmailService.GetNotificationEmailsByUserIdsAsync(allUserIds, ct);

        IReadOnlySet<Guid>? searchUserIds = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Admin auth gated by [Authorize] above; limit ~== "every match" at ~500-user scale.
            var searchResults = await _userService.SearchUsersAsync(
                search, PersonSearchFields.AdminAll, limit: 500, ct);

            // Matcher covers UserEmail rows only — union User.Email match for parity.
            var byEmail = allUsers
                .Where(u =>
                    (u.Email ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    u.BurnerName.Contains(search, StringComparison.OrdinalIgnoreCase))
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
            membershipCalculator,
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
        var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(info.Id, ct);
        var pendingConsentCount = snapshot.PendingConsentCount;

        var applications = await applicationDecisionService.GetUserApplicationsAsync(info.Id, ct);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

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
            OnsiteSince = await ResolveOnsiteSinceAsync(info, ct),
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
    private async Task<Instant?> ResolveOnsiteSinceAsync(UserInfo info, CancellationToken ct)
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
        var externalLogins = await userManager.GetLoginsAsync(user);

        var viewModel = ProfileEditViewModelBuilder.Build(
            info,
            applications,
            allShiftTags,
            preferredShiftTags,
            preview,
            externalLogins.Any(l => string.Equals(l.LoginProvider, "Google", StringComparison.OrdinalIgnoreCase)),
            p => Url.Action(nameof(Picture), new { id = p.Id, v = p.UpdatedAt.ToUnixTimeTicks() }));

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
        return View(viewModel);
    }

    [HttpPost("Me/Edit")]
    [ValidateAntiForgeryToken]
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

        var profileId = await profileService.SaveProfileAsync(
            user.Id, model.BurnerName, saveRequest,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        // Peer-call into Onboarding; ProfileService doesn't.
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

        await profileService.SaveCVEntriesAsync(user.Id, cvEntries);

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

        await profileService.SaveProfileLanguagesAsync(profileId, newLanguages);

        await shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, model.EditableShiftTagIds);

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
        var result = ResizeProfilePicture(uploadStream.ToArray(), uploadContentType);
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

        await emailService.SendEmailVerificationAsync(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            user.PreferredLanguage);

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
        var rawRows = await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct);
        var matching = rawRows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal)
            && string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        // Auth-method invariant: at least one verified UserEmail must remain after unlink (server source-of-truth).
        var verifiedTotal = rawRows.Count(r => r.IsVerified);
        var verifiedAfter = verifiedTotal - (matching?.IsVerified == true ? 1 : 0);
        if (verifiedAfter < 1)
        {
            SetError(localizer["LinkedAccounts_UnlinkBlockedLastSignInMethod"].Value);
            return RedirectToAction(nameof(Emails));
        }

        try
        {
            if (matching is not null)
            {
                var ok = await userEmailService.UnlinkAsync(user.Id, matching.Id, user.Id, ct);
                SetEmailUnlinkedResult(ok);
            }
            else
            {
                // Orphan login: no UserEmail row — drop directly.
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
            SetError(ex.Message);
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

        await emailService.SendEmailVerificationAsync(
            trimmedEmail,
            info?.BurnerName ?? string.Empty,
            verificationUrl!,
            result.IsConflict,
            targetUser.PreferredLanguage,
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

        var result = await accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal))
                SetError(localizer["Profile_NoDeletionPending"].Value);
            return RedirectToAction(nameof(Privacy));
        }

        SetSuccess(localizer["Profile_DeletionCancelled"].Value);
        return RedirectToAction(nameof(Privacy));
    }

    [HttpGet("Me/ShiftInfo")]
    public async Task<IActionResult> ShiftInfo()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound(); var profile = await shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);
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
        var fileName = $"nobodies-profiles-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToIsoDateString()}.json";

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
        // §2: controller routes through IProfileService (owns FS-first/DB-fallback + GDPR gate, see #527).
        var result = await profileService.GetProfilePictureAsync(id, ct);
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
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        var externalLogins = await userManager.GetLoginsAsync(user);
        if (!HasGoogleAvatarSource(user, externalLogins))
        {
            SetError(localizer["Profile_ImportGooglePhoto_Unavailable"].Value);
            return RedirectToAction(nameof(Edit));
        }

        var info = await _userService.GetUserInfoAsync(user.Id, ct);
        if (info?.Profile is null)
        {
            SetError(localizer["Profile_ImportGooglePhoto_NoProfile"].Value);
            return RedirectToAction(nameof(Edit));
        }
        if (info.Profile.HasCustomPicture)
        {
            SetError(localizer["Profile_ImportGooglePhoto_AlreadyHasCustom"].Value);
            return RedirectToAction(nameof(Edit));
        }

        if (!TryGetTrustedGoogleAvatarUri(user, out var pictureUri))
        {
            SetError(localizer["Profile_ImportGooglePhoto_NotGoogleUrl"].Value);
            return RedirectToAction(nameof(Edit));
        }

        var rawBytes = await FetchGoogleAvatarBytesAsync(pictureUri, user.Id, ct);
        if (rawBytes is null)
        {
            return RedirectToAction(nameof(Edit));
        }

        var resized = Helpers.ProfilePictureProcessor.ResizeProfilePicture(rawBytes, logger);
        if (resized is null)
        {
            SetError(localizer["Profile_ImportGooglePhoto_InvalidFormat"].Value);
            return RedirectToAction(nameof(Edit));
        }

        await profileService.SetProfilePictureAsync(user.Id, resized.Value.Data, resized.Value.ContentType, ct);

        logger.LogInformation("Imported Google avatar for user {UserId}", user.Id);
        SetSuccess(localizer["Profile_ImportGooglePhoto_Success"].Value);
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

        logger.LogWarning(
            "Refusing to import Google photo for user {UserId}: URL is not a trusted Google host",
            user.Id);
        return false;
    }

    private async Task<byte[]?> FetchGoogleAvatarBytesAsync(Uri pictureUri, Guid userId, CancellationToken ct)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(GoogleAvatarHttpClientName);
            using var response = await httpClient.GetAsync(pictureUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Google avatar fetch for user {UserId} returned {StatusCode}",
                    userId, (int)response.StatusCode);
                SetError(localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return null;
            }

            var fetchedContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AllowedImageContentTypes.Contains(fetchedContentType))
            {
                logger.LogWarning(
                    "Google avatar for user {UserId} returned unsupported content type {ContentType}",
                    userId, fetchedContentType);
                SetError(localizer["Profile_ImportGooglePhoto_InvalidFormat"].Value);
                return null;
            }

            var rawBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (rawBytes.Length == 0 || rawBytes.Length > MaxGooglePhotoDownloadBytes)
            {
                logger.LogWarning(
                    "Google avatar for user {UserId} had invalid size {Bytes}", userId, rawBytes.Length);
                SetError(localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
                return null;
            }

            return rawBytes;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Google avatar fetch failed for user {UserId}", userId);
            SetError(localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Google avatar fetch timed out for user {UserId}", userId);
            SetError(localizer["Profile_ImportGooglePhoto_FetchFailed"].Value);
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

        // Onsite chip visibility (#736): self always, plus the same admin/board
        // policy that gates /Tickets/Admin/Onsite. Coordinators below board
        // tier don't see the chip on other humans — wider visibility is a
        // follow-up PR if needed.
        var canViewOnsiteChip = isOwnProfile
            || (await authorizationService.AuthorizeAsync(
                User, PolicyNames.TicketAdminBoardOrAdmin)).Succeeded;

        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profileInfo!.BurnerName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowContext.History,
            CanViewShiftSignups = noShowContext.CanView,
            OnsiteSince = canViewOnsiteChip
                ? await ResolveOnsiteSinceAsync(profileInfo, ct)
                : null,
            CanViewOnsiteChip = canViewOnsiteChip,
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
                MarkedByName = reviewer?.BurnerName,
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

        var memberships = await teamService.GetActiveTeamMembershipsForUserAsync(id, ct);
        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships);

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
            RecipientDisplayName = targetInfo.BurnerName
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

        await emailService.SendFacilitatedMessageAsync(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.CleanMessage,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage);

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
        var results = await _userService.SearchUsersAsync(
            q!, PersonSearchFields.PublicAll, limit: 50, ct);

        // Display sort at controller — memory/architecture/display-sort-in-controllers.md.
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

    // ─── Admin: Role Assignment Roster ───────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("Admin/Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = clock.GetCurrentInstant();

        var (assignments, totalCount) = await roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now);

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.UserEmail ?? string.Empty,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByDisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            RoleFilter = role,
            ShowInactive = showInactive,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Shared/Roles.cshtml", viewModel);
    }

    // ─── Admin: Per-Person Detail ────────────────────────────────────

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin")]
    public async Task<IActionResult> AdminDetail(Guid id, CancellationToken ct)
    {
        // Per-section composition (see #685) — Profile reads its own row; cross-section data fetched here.
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null)
            return NotFound();

        var applications = await applicationDecisionService.GetUserApplicationsAsync(id, ct);
        var userEmails = await userEmailService.GetEntitiesByUserIdAsync(id, ct);
        var consentCount = await consentService.GetConsentRecordCountAsync(id, ct);
        var roleAssignments = await roleAssignmentService.GetByUserIdAsync(id, ct);
        var roleCreatorNamesByUserId = (await _userService.GetUserInfosAsync(
                roleAssignments.Select(ra => ra.CreatedByUserId).Distinct().ToList(), ct))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.BurnerName);
        var campaignGrants = await campaignService.GetAllGrantsForUserAsync(id, ct);
        var outboxCount = await emailOutboxService.GetMessageCountForUserAsync(id, ct);
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
            clock.GetCurrentInstant(),
            await GetRejectedByNameAsync(info.Profile, ct),
            revealedIban);

        return View("AdminDetail", viewModel);
    }

    private async Task<string?> GetRejectedByNameAsync(ProfileInfo? profile, CancellationToken ct)
    {
        if (profile?.RejectedByUserId is null)
            return null;

        var rejectedByInfo = await _userService.GetUserInfoAsync(profile.RejectedByUserId.Value, ct);
        return rejectedByInfo?.BurnerName;
    }

    // Reveals unmasked IBAN once (TempData) + audit. Admin-only.
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
        await auditLogService.LogAsync(
            AuditAction.IbanReveal, "User", id,
            $"Admin revealed IBAN for user {id}", actorId);
        TempData["RevealedIban"] = iban;
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("{id:guid}/Admin/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id, CancellationToken ct)
    {
        var messages = await emailOutboxService.GetMessagesForUserAsync(id, ct);

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

        var result = await humanLifecycleService.SuspendAsync(id, currentUser.Id, notes);
        if (!result.Success)
            return NotFound();

        SetSuccess(localizer["Admin_MemberSuspended"].Value);
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

        var result = await humanLifecycleService.UnsuspendAsync(id, currentUser.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess(localizer["Admin_MemberUnsuspended"].Value);
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

        var result = await onboardingService.ApproveVolunteerAsync(id, currentUser.Id);
        if (!result.Success)
            return NotFound();

        SetSuccess(localizer["Admin_VolunteerApproved"].Value);
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

        var result = await onboardingService.RejectSignupAsync(id, currentUser.Id, reason);
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
        var info = await _userService.GetUserInfoAsync(id);
        if (info is null)
        {
            return NotFound();
        }

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)]
        };

        return View(viewModel);
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        var info = await _userService.GetUserInfoAsync(id);
        if (info is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            PopulateRoleAssignmentForm(model, id);
            return View(model);
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, model.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for role assignment: principal {Principal} attempted to assign role {Role} to user {UserId}",
                User.Identity?.Name, model.RoleName, id);
            return Forbid();
        }

        var result = await roleAssignmentService.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, model.Notes);

        SetRoleAssignmentResult(model.RoleName, result.Success);
        return RedirectToAction(nameof(AdminDetail), new { id });
    }

    private void PopulateRoleAssignmentForm(CreateRoleAssignmentViewModel model, Guid userId)
    {
        model.UserId = userId;
        model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
    }

    private void SetRoleAssignmentResult(string roleName, bool success)
    {
        if (success)
        {
            SetSuccess(string.Format(localizer["Admin_RoleAssigned"].Value, roleName));
            return;
        }

        SetError(string.Format(localizer["Admin_RoleAlreadyActive"].Value, roleName));
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpPost("{id:guid}/Admin/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment is null)
        {
            // NotFound, not Unauthorized — prevents role-assignment enumeration.
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for ending role: principal {Principal} attempted to end role {Role} for user {UserId}",
                User.Identity?.Name, roleAssignment.RoleName, roleAssignment.UserId);
            return NotFound();
        }

        var result = await roleAssignmentService.EndRoleAsync(
            roleId, currentUser.Id, notes);

        SetRoleEndedResult(result.Success, roleAssignment.RoleName, roleAssignment.UserDisplayName);
        return RedirectToAction(nameof(AdminDetail), new { id = roleAssignment.UserId });
    }


    private void SetRoleEndedResult(bool success, string roleName, string userDisplayName)
    {
        if (success)
        {
            SetSuccess(string.Format(localizer["Admin_RoleEnded"].Value, roleName, userDisplayName));
            return;
        }

        SetError(localizer["Admin_RoleNotActive"].Value);
    }
    // ─── Helpers ─────────────────────────────────────────────────────

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData, string contentType) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, logger);

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
                await profileService.GetEmailCooldownInfoAsync(pendingEmail.Id, ct);
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
                && User.IsInRole(Domain.Constants.RoleNames.Admin)
                ? user.IdentityEmailColumn
                : null,
            TargetUserInfo = isAdminContext
                && User.IsInRole(Domain.Constants.RoleNames.Admin)
                    ? info
                    : null,
        };
    }

}





