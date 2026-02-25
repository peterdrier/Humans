using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions;
using Humans.Web.Models;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Web.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<ProfileController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IContactFieldService _contactFieldService;
    private readonly VolunteerHistoryService _volunteerHistoryService;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMemoryCache _cache;
    private readonly IStringLocalizer<SharedResource> _localizer;

    private const int VerificationCooldownMinutes = 5;
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
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ProfileController> logger,
        IConfiguration configuration,
        IContactFieldService contactFieldService,
        VolunteerHistoryService volunteerHistoryService,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        IUserEmailService userEmailService,
        IAuditLogService auditLogService,
        IMemoryCache cache,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _configuration = configuration;
        _contactFieldService = contactFieldService;
        _volunteerHistoryService = volunteerHistoryService;
        _emailService = emailService;
        _membershipCalculator = membershipCalculator;
        _userEmailService = userEmailService;
        _auditLogService = auditLogService;
        _cache = cache;
        _localizer = localizer;
    }

    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        // Get consent status for the alert banners (profile card handles its own data).
        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(user.Id);

        // Get latest tier application for the profile banner
        var latestApplication = await _dbContext.Applications
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .FirstOrDefaultAsync();

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            UserId = user.Id,
            HasPendingConsents = membershipSnapshot.PendingConsentCount > 0,
            PendingConsentCount = membershipSnapshot.PendingConsentCount,
            IsApproved = profile?.IsApproved ?? false,
            IsOwnProfile = true,
            DisplayName = user.DisplayName,
        };

        // Show tier application status (skip Withdrawn — not interesting)
        if (latestApplication != null && latestApplication.Status != ApplicationStatus.Withdrawn)
        {
            viewModel.TierApplicationStatus = latestApplication.Status.ToString();
            viewModel.TierApplicationTier = latestApplication.MembershipTier;
            viewModel.TierApplicationBadgeClass = latestApplication.Status.GetBadgeClass();
        }

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Edit([FromQuery] bool preview = false)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        // Get all contact fields for editing
        var contactFields = profile != null
            ? await _contactFieldService.GetAllContactFieldsAsync(profile.Id)
            : [];

        // Get all volunteer history entries for editing
        var volunteerHistory = profile != null
            ? await _volunteerHistoryService.GetAllAsync(profile.Id)
            : [];

        var hasCustomPicture = profile?.HasCustomProfilePicture == true;

        // Initial setup = no profile or not yet approved (onboarding)
        // ?preview=true forces initial-setup mode for testing
        var isInitialSetup = profile == null || !profile.IsApproved || preview;

        // Check if tier is locked (has active/pending application)
        var hasPendingOrApprovedApplication = profile != null && await _dbContext.Applications
            .AnyAsync(a => a.UserId == user.Id &&
                (a.Status == ApplicationStatus.Submitted ||
                 a.Status == ApplicationStatus.Approved));

        // Load existing pending application for inline display during initial setup
        MemberApplication? pendingApplication = null;
        if (isInitialSetup)
        {
            pendingApplication = await _dbContext.Applications
                .Where(a => a.UserId == user.Id &&
                    a.Status == ApplicationStatus.Submitted)
                .FirstOrDefaultAsync();
        }

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
            IsTierLocked = hasPendingOrApprovedApplication,
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
                DateString = vh.Date.ToString("yyyy-MM-dd", null),
                EventName = vh.EventName,
                Description = vh.Description
            }).ToList()
        };

        ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ProfileViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var now = _clock.GetCurrentInstant();

        if (profile == null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Profiles.Add(profile);
            await _dbContext.SaveChangesAsync(); // Save to get the profile ID for contact fields
        }

        profile.BurnerName = model.BurnerName;
        profile.FirstName = model.FirstName;
        profile.LastName = model.LastName;
        profile.City = model.City;
        profile.CountryCode = model.CountryCode;
        profile.Latitude = model.Latitude;
        profile.Longitude = model.Longitude;
        profile.PlaceId = model.PlaceId;
        profile.Bio = model.Bio?.TrimEnd();
        profile.Pronouns = model.Pronouns;
        profile.ContributionInterests = model.ContributionInterests?.TrimEnd();
        profile.BoardNotes = model.BoardNotes?.TrimEnd();
        profile.DateOfBirth = model.ParsedBirthday;
        profile.EmergencyContactName = model.EmergencyContactName;
        profile.EmergencyContactPhone = model.EmergencyContactPhone;
        profile.EmergencyContactRelationship = model.EmergencyContactRelationship;
        profile.NoPriorBurnExperience = model.NoPriorBurnExperience;
        profile.UpdatedAt = now;

        // Handle profile picture removal
        if (model.RemoveProfilePicture)
        {
            profile.ProfilePictureData = null;
            profile.ProfilePictureContentType = null;
        }

        // Handle profile picture upload
        if (model.ProfilePictureUpload is { Length: > 0 })
        {
            if (model.ProfilePictureUpload.Length > MaxProfilePictureUploadBytes)
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureTooLarge"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                return View(model);
            }

            // Browsers often send application/octet-stream for HEIC/HEIF files;
            // resolve the actual content type from the file extension in that case.
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
                ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                return View(model);
            }

            using var uploadStream = new MemoryStream();
            await model.ProfilePictureUpload.CopyToAsync(uploadStream);
            var result = ResizeProfilePicture(uploadStream.ToArray(), uploadContentType);
            if (result == null)
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureInvalidFormat"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                return View(model);
            }

            profile.ProfilePictureData = result.Value.Data;
            profile.ProfilePictureContentType = result.Value.ContentType;
        }

        // Validate Burner CV: must have entries OR check "no prior experience"
        var hasVolunteerHistory = model.EditableVolunteerHistory
            .Any(vh => !string.IsNullOrWhiteSpace(vh.EventName) && vh.ParsedDate.HasValue);
        if (!model.NoPriorBurnExperience && !hasVolunteerHistory)
        {
            ModelState.AddModelError(nameof(model.NoPriorBurnExperience),
                _localizer["Profile_BurnerCVRequired"].Value);
            model.IsInitialSetup = !profile.IsApproved;
            model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                && string.IsNullOrEmpty(model.LastName)
                && string.IsNullOrEmpty(model.EmergencyContactName);
            ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
            return View(model);
        }

        // Handle tier selection during initial setup
        var isInitialSetup = !profile.IsApproved;
        if (isInitialSetup)
        {
            // Server-side enforcement: don't allow tier changes if application exists
            var hasPendingOrApprovedApp = await _dbContext.Applications
                .AnyAsync(a => a.UserId == user.Id &&
                    (a.Status == ApplicationStatus.Submitted ||
                     a.Status == ApplicationStatus.Approved));
            if (hasPendingOrApprovedApp)
            {
                model.SelectedTier = profile.MembershipTier;
            }

            // profile.MembershipTier = model.SelectedTier; // Removed: Tier is set only on Board approval

            // Validate motivation is required for Colaborador/Asociado
            if (model.SelectedTier != MembershipTier.Volunteer &&
                string.IsNullOrWhiteSpace(model.ApplicationMotivation))
            {
                ModelState.AddModelError(nameof(model.ApplicationMotivation),
                    _localizer["Profile_MotivationRequired"].Value);
                model.IsInitialSetup = true;
                model.ShowPrivateFirst = string.IsNullOrEmpty(model.FirstName)
                    && string.IsNullOrEmpty(model.LastName)
                    && string.IsNullOrEmpty(model.EmergencyContactName);
                ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                return View(model);
            }

            // Validate Asociado-specific fields
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
                    ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                    return View(model);
                }
            }

            // Auto-create or update inline application for Colaborador/Asociado
            if (model.SelectedTier != MembershipTier.Volunteer)
            {
                var existingApp = await _dbContext.Applications
                    .FirstOrDefaultAsync(a => a.UserId == user.Id &&
                        a.Status == ApplicationStatus.Submitted);

                if (existingApp != null)
                {
                    // Update existing pending application
                    existingApp.Motivation = model.ApplicationMotivation!;
                    existingApp.AdditionalInfo = model.ApplicationAdditionalInfo;
                    existingApp.MembershipTier = model.SelectedTier;
                    existingApp.SignificantContribution = model.SelectedTier == MembershipTier.Asociado
                        ? model.ApplicationSignificantContribution : null;
                    existingApp.RoleUnderstanding = model.SelectedTier == MembershipTier.Asociado
                        ? model.ApplicationRoleUnderstanding : null;
                    existingApp.UpdatedAt = now;
                }
                else
                {
                    // Create new application
                    var application = new MemberApplication
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        MembershipTier = model.SelectedTier,
                        Motivation = model.ApplicationMotivation!,
                        AdditionalInfo = model.ApplicationAdditionalInfo,
                        SignificantContribution = model.SelectedTier == MembershipTier.Asociado
                            ? model.ApplicationSignificantContribution : null,
                        RoleUnderstanding = model.SelectedTier == MembershipTier.Asociado
                            ? model.ApplicationRoleUnderstanding : null,
                        Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                        SubmittedAt = now,
                        UpdatedAt = now
                    };
                    _dbContext.Applications.Add(application);
                }
            }
        }

        // Update display name on user to burner name (public-facing name)
        user.DisplayName = model.BurnerName;
        await _userManager.UpdateAsync(user);

        await _dbContext.SaveChangesAsync();
        _cache.Remove(CacheKeys.NavBadgeCounts);

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
            await _contactFieldService.SaveContactFieldsAsync(profile.Id, contactFieldDtos);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
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

        await _volunteerHistoryService.SaveAsync(profile.Id, volunteerHistoryDtos);

        // If profile was just created, check if user already has all required consents
        // (they may have consented before creating their profile)
        if (!profile.IsApproved && profile.ConsentCheckStatus == null)
        {
            var hasAllConsents = await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
                user.Id, SystemTeamIds.Volunteers);
            if (hasAllConsents)
            {
                profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
                profile.UpdatedAt = _clock.GetCurrentInstant();
                await _dbContext.SaveChangesAsync();
                _cache.Remove(CacheKeys.NavBadgeCounts);
                _logger.LogInformation(
                    "User {UserId} has all consents signed, consent check set to Pending", user.Id);
            }
        }

        _logger.LogInformation("User {UserId} updated their profile", user.Id);

        TempData["SuccessMessage"] = _localizer["Profile_Updated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id)
    {
        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.ProfilePictureData, p.ProfilePictureContentType })
            .FirstOrDefaultAsync();

        if (profile?.ProfilePictureData == null || string.IsNullOrEmpty(profile.ProfilePictureContentType))
        {
            return NotFound();
        }

        return File(profile.ProfilePictureData, profile.ProfilePictureContentType);
    }

    private (byte[] Data, string ContentType)? ResizeProfilePicture(byte[] imageData, string contentType) =>
        Helpers.ProfilePictureProcessor.ResizeProfilePicture(imageData, contentType, _logger);

    [HttpGet]
    public async Task<IActionResult> Emails()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var viewModel = await BuildEmailsViewModelAsync(user);
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEmail(EmailsViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.NewEmail))
        {
            ModelState.AddModelError(nameof(model.NewEmail), _localizer["Profile_EnterEmail"].Value);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        try
        {
            var token = await _userEmailService.AddEmailAsync(user.Id, model.NewEmail);

            // Build verification URL
            var verificationUrl = Url.Action(
                nameof(VerifyEmail),
                "Profile",
                new { userId = user.Id, token = HttpUtility.UrlEncode(token) },
                Request.Scheme);

            // Send verification email
            await _emailService.SendEmailVerificationAsync(
                model.NewEmail.Trim(),
                user.DisplayName,
                verificationUrl!,
                user.PreferredLanguage);

            _logger.LogInformation(
                "Sent email verification to {Email} for user {UserId}",
                model.NewEmail, user.Id);

            TempData["SuccessMessage"] = string.Format(CultureInfo.CurrentCulture, _localizer["Profile_VerificationSent"].Value, model.NewEmail.Trim());
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(nameof(model.NewEmail), ex.Message);
            return View(nameof(Emails), await BuildEmailsViewModelAsync(user));
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpGet]
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
            var verifiedEmail = await _userEmailService.VerifyEmailAsync(userId, decodedToken);

            _logger.LogInformation(
                "User {UserId} verified email {Email}",
                userId, verifiedEmail);

            ViewData["Success"] = true;
            ViewData["Message"] = string.Format(_localizer["Profile_EmailVerified"].Value, verifiedEmail);
            return View("VerifyEmailResult");
        }
        catch (InvalidOperationException)
        {
            return VerifyEmailError(_localizer["Profile_InvalidVerificationLink"].Value);
        }
        catch (ValidationException ex)
        {
            return VerifyEmailError(ex.Message);
        }
    }

    private IActionResult VerifyEmailError(string message)
    {
        ViewData["Success"] = false;
        ViewData["Message"] = message;
        return View("VerifyEmailResult");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNotificationTarget(Guid emailId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        try
        {
            await _userEmailService.SetNotificationTargetAsync(user.Id, emailId);
            TempData["SuccessMessage"] = _localizer["Profile_NotificationTargetUpdated"].Value;
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEmailVisibility(Guid emailId, string? visibility)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        ContactFieldVisibility? parsedVisibility = null;
        if (!string.IsNullOrEmpty(visibility) && Enum.TryParse<ContactFieldVisibility>(visibility, out var v))
        {
            parsedVisibility = v;
        }

        try
        {
            await _userEmailService.SetVisibilityAsync(user.Id, emailId, parsedVisibility);
            TempData["SuccessMessage"] = _localizer["Profile_EmailVisibilityUpdated"].Value;
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Emails));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEmail(Guid emailId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        try
        {
            await _userEmailService.DeleteEmailAsync(user.Id, emailId);
            TempData["SuccessMessage"] = _localizer["Profile_EmailDeleted"].Value;
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Emails));
    }

    private async Task<EmailsViewModel> BuildEmailsViewModelAsync(User user)
    {
        var emails = await _userEmailService.GetUserEmailsAsync(user.Id);

        // Check cooldown for adding new emails
        var now = _clock.GetCurrentInstant();
        var canAdd = true;
        var minutesUntilResend = 0;

        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail != null)
        {
            // Look up the actual verification sent time from DB
            var pendingRecord = await _dbContext.UserEmails
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == pendingEmail.Id);

            if (pendingRecord?.VerificationSentAt.HasValue == true)
            {
                var cooldownEnd = pendingRecord.VerificationSentAt.Value.Plus(Duration.FromMinutes(VerificationCooldownMinutes));
                if (now < cooldownEnd)
                {
                    canAdd = false;
                    minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                }
            }
        }

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
                IsPendingVerification = e.IsPendingVerification
            }).ToList(),
            CanAddEmail = canAdd,
            MinutesUntilResend = minutesUntilResend
        };
    }

    [HttpGet]
    public async Task<IActionResult> Privacy()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var viewModel = new PrivacyViewModel
        {
            IsDeletionPending = user.IsDeletionPending,
            DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc(),
            DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc()
        };

        ViewData["DpoEmail"] = _configuration["Email:DpoAddress"];
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (user.IsDeletionPending)
        {
            TempData["ErrorMessage"] = _localizer["Profile_DeletionAlreadyPending"].Value;
            return RedirectToAction(nameof(Privacy));
        }

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = deletionDate;
        await _userManager.UpdateAsync(user);

        // Revoke team memberships and role assignments immediately
        await _dbContext.Entry(user).Collection(u => u.TeamMemberships).LoadAsync();
        await _dbContext.Entry(user).Collection(u => u.RoleAssignments).LoadAsync();

        var endedMemberships = 0;
        foreach (var membership in user.TeamMemberships.Where(m => m.LeftAt == null))
        {
            membership.LeftAt = now;
            endedMemberships++;
        }

        var endedRoles = 0;
        foreach (var role in user.RoleAssignments.Where(r => r.ValidTo == null))
        {
            role.ValidTo = now;
            endedRoles++;
        }

        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, "User", user.Id,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            user.Id, user.DisplayName);

        await _dbContext.SaveChangesAsync();

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}. " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            user.Id, deletionDate, endedMemberships, endedRoles);

        // Send confirmation email - load UserEmails for GetEffectiveEmail()
        await _dbContext.Entry(user).Collection(u => u.UserEmails).LoadAsync();
        var effectiveEmail = user.GetEffectiveEmail();
        if (effectiveEmail != null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                effectiveEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
                user.PreferredLanguage,
                CancellationToken.None);
        }

        TempData["SuccessMessage"] = string.Format(CultureInfo.CurrentCulture, _localizer["Profile_DeletionRequested"].Value, deletionDate.ToDateTimeUtc().ToString("MMMM d, yyyy", CultureInfo.CurrentCulture));
        return RedirectToAction(nameof(Privacy));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (!user.IsDeletionPending)
        {
            TempData["ErrorMessage"] = _localizer["Profile_NoDeletionPending"].Value;
            return RedirectToAction(nameof(Privacy));
        }

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("User {UserId} cancelled account deletion request", user.Id);

        TempData["SuccessMessage"] = _localizer["Profile_DeletionCancelled"].Value;
        return RedirectToAction(nameof(Privacy));
    }

    private async Task<IActionResult> ExportData()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        var applications = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync();

        var consents = await _dbContext.ConsentRecords
            .AsNoTracking()
            .Include(c => c.DocumentVersion)
                .ThenInclude(v => v.LegalDocument)
            .Where(c => c.UserId == user.Id)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync();

        var teamMemberships = await _dbContext.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Where(tm => tm.UserId == user.Id)
            .OrderByDescending(tm => tm.JoinedAt)
            .ToListAsync();

        var contactFields = profile != null
            ? await _dbContext.ContactFields
                .AsNoTracking()
                .Where(cf => cf.ProfileId == profile.Id)
                .OrderBy(cf => cf.DisplayOrder)
                .ToListAsync()
            : [];

        var roleAssignments = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == user.Id)
            .ToListAsync();

        var userEmails = await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .OrderBy(e => e.DisplayOrder)
            .ToListAsync();

        var export = new
        {
            ExportedAt = _clock.GetCurrentInstant().ToString(null, CultureInfo.InvariantCulture),
            Account = new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                CreatedAt = user.CreatedAt.ToString(null, CultureInfo.InvariantCulture),
                LastLoginAt = user.LastLoginAt?.ToString(null, CultureInfo.InvariantCulture)
            },
            UserEmails = userEmails.Select(e => new
            {
                e.Email,
                e.IsVerified,
                e.IsOAuth,
                e.IsNotificationTarget,
                e.Visibility
            }),
            Profile = profile != null ? new
            {
                profile.BurnerName,
                profile.FirstName,
                profile.LastName,
                Birthday = profile.DateOfBirth != null ? $"{profile.DateOfBirth.Value.Month:D2}-{profile.DateOfBirth.Value.Day:D2}" : null,
                profile.City,
                profile.CountryCode,
                profile.Bio,
                profile.Pronouns,
                profile.ContributionInterests,
                profile.BoardNotes,
                profile.IsSuspended,
                profile.EmergencyContactName,
                profile.EmergencyContactPhone,
                profile.EmergencyContactRelationship,
                profile.HasCustomProfilePicture,
                CreatedAt = profile.CreatedAt.ToString(null, CultureInfo.InvariantCulture),
                UpdatedAt = profile.UpdatedAt.ToString(null, CultureInfo.InvariantCulture)
            } : null,
            ContactFields = contactFields.Select(cf => new
            {
                cf.FieldType,
                Label = cf.DisplayLabel,
                cf.Value,
                cf.Visibility
            }),
            Applications = applications.Select(a => new
            {
                a.Id,
                a.Status,
                a.MembershipTier,
                a.Motivation,
                a.AdditionalInfo,
                a.SignificantContribution,
                a.RoleUnderstanding,
                SubmittedAt = a.SubmittedAt.ToString(null, CultureInfo.InvariantCulture),
                ResolvedAt = a.ResolvedAt?.ToString(null, CultureInfo.InvariantCulture)
            }),
            Consents = consents.Select(c => new
            {
                DocumentName = c.DocumentVersion.LegalDocument.Name,
                DocumentVersion = c.DocumentVersion.VersionNumber,
                c.ExplicitConsent,
                ConsentedAt = c.ConsentedAt.ToString(null, CultureInfo.InvariantCulture),
                c.IpAddress,
                c.UserAgent
            }),
            TeamMemberships = teamMemberships.Select(tm => new
            {
                TeamName = tm.Team.Name,
                tm.Role,
                JoinedAt = tm.JoinedAt.ToString(null, CultureInfo.InvariantCulture),
                LeftAt = tm.LeftAt?.ToString(null, CultureInfo.InvariantCulture)
            }),
            RoleAssignments = roleAssignments.Select(ra => new
            {
                ra.RoleName,
                ValidFrom = ra.ValidFrom.ToString(null, CultureInfo.InvariantCulture),
                ValidTo = ra.ValidTo?.ToString(null, CultureInfo.InvariantCulture),
                ra.CreatedByUserId
            })
        };

        _logger.LogInformation("User {UserId} exported their data", user.Id);

        return Json(export, ExportJsonOptions);
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        // Rate limit: one export per hour
        // For simplicity, we just return the data - implement rate limiting if needed

        var result = await ExportData() as JsonResult;
        if (result?.Value == null)
        {
            return NotFound();
        }

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value,
            ExportJsonOptions);

        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"nobodies-profiles-export-{DateTime.UtcNow:yyyy-MM-dd}.json";

        return File(bytes, "application/json", fileName);
    }
}
