using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using SkiaSharp;
using Humans.Web.Models;

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
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IUserEmailService _userEmailService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";
    private const int VerificationCooldownMinutes = 5;
    private const int MaxProfilePictureUploadBytes = 20 * 1024 * 1024; // 20MB upload limit
    private const int MaxProfilePictureLongSide = 1000;
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
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
        ITeamService teamService,
        IMembershipCalculator membershipCalculator,
        IUserEmailService userEmailService,
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
        _teamService = teamService;
        _membershipCalculator = membershipCalculator;
        _userEmailService = userEmailService;
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
            .FirstOrDefaultAsync(p => p.UserId == user.Id);

        // Get canonical membership + consent status in one call.
        var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(user.Id);
        var pendingConsents = membershipSnapshot.PendingConsentCount;

        // Get contact fields (user viewing their own profile sees all)
        var contactFields = profile != null
            ? await _contactFieldService.GetVisibleContactFieldsAsync(profile.Id, user.Id)
            : [];

        // Get visible user emails (owner sees all visible emails)
        var visibleEmails = await _userEmailService.GetVisibleEmailsAsync(
            user.Id, ContactFieldVisibility.BoardOnly);

        // Get volunteer history entries
        var volunteerHistory = profile != null
            ? await _volunteerHistoryService.GetAllAsync(profile.Id)
            : [];

        // Get user's teams (excluding Volunteers system team)
        var userTeams = await _teamService.GetUserTeamsAsync(user.Id);
        var displayableTeams = userTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .OrderBy(tm => tm.Team.Name, StringComparer.Ordinal)
            .Select(tm => new TeamMembershipViewModel
            {
                TeamId = tm.TeamId,
                TeamName = tm.Team.Name,
                TeamSlug = tm.Team.Slug,
                IsLead = tm.Role == TeamMemberRole.Lead,
                IsSystemTeam = tm.Team.IsSystemTeam
            })
            .ToList();

        var hasCustomPicture = profile?.HasCustomProfilePicture == true;

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture ? Url.Action(nameof(Picture), new { id = profile!.Id }) : null,
            BurnerName = profile?.BurnerName ?? string.Empty,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Bio = profile?.Bio,
            Pronouns = profile?.Pronouns,
            BirthdayMonth = profile?.DateOfBirth?.Month,
            BirthdayDay = profile?.DateOfBirth?.Day,
            EmergencyContactName = profile?.EmergencyContactName,
            EmergencyContactPhone = profile?.EmergencyContactPhone,
            EmergencyContactRelationship = profile?.EmergencyContactRelationship,
            HasPendingConsents = pendingConsents > 0,
            PendingConsentCount = pendingConsents,
            IsApproved = profile?.IsApproved ?? false,
            MembershipStatus = membershipSnapshot.Status.ToString(),
            IsOwnProfile = true,
            CanViewLegalName = true, // User viewing their own profile
            UserEmails = visibleEmails.Select(e => new UserEmailDisplayViewModel
            {
                Email = e.Email,
                IsNotificationTarget = e.IsNotificationTarget
            }).ToList(),
            ContactFields = contactFields.Select(cf => new ContactFieldViewModel
            {
                Id = cf.Id,
                FieldType = cf.FieldType,
                Label = cf.Label,
                Value = cf.Value,
                Visibility = cf.Visibility
            }).ToList(),
            VolunteerHistory = volunteerHistory.Select(vh => new VolunteerHistoryEntryViewModel
            {
                Id = vh.Id,
                Date = vh.Date,
                EventName = vh.EventName,
                Description = vh.Description
            }).ToList(),
            Teams = displayableTeams
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Edit()
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

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            HasCustomProfilePicture = hasCustomPicture,
            CustomProfilePictureUrl = hasCustomPicture ? Url.Action(nameof(Picture), new { id = profile!.Id }) : null,
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
            BirthdayMonth = profile?.DateOfBirth?.Month,
            BirthdayDay = profile?.DateOfBirth?.Day,
            EmergencyContactName = profile?.EmergencyContactName,
            EmergencyContactPhone = profile?.EmergencyContactPhone,
            EmergencyContactRelationship = profile?.EmergencyContactRelationship,
            CanViewLegalName = true, // User editing their own profile
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
        profile.Bio = model.Bio;
        profile.Pronouns = model.Pronouns;
        profile.DateOfBirth = model.ParsedBirthday;
        profile.EmergencyContactName = model.EmergencyContactName;
        profile.EmergencyContactPhone = model.EmergencyContactPhone;
        profile.EmergencyContactRelationship = model.EmergencyContactRelationship;
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

            if (!AllowedImageContentTypes.Contains(model.ProfilePictureUpload.ContentType))
            {
                ModelState.AddModelError(nameof(model.ProfilePictureUpload),
                    _localizer["Profile_PictureInvalidFormat"].Value);
                ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];
                return View(model);
            }

            using var uploadStream = new MemoryStream();
            await model.ProfilePictureUpload.CopyToAsync(uploadStream);
            var (resizedData, contentType) = ResizeProfilePicture(uploadStream.ToArray());
            profile.ProfilePictureData = resizedData;
            profile.ProfilePictureContentType = contentType;
        }

        // Update display name on user to burner name (public-facing name)
        user.DisplayName = model.BurnerName;
        await _userManager.UpdateAsync(user);

        await _dbContext.SaveChangesAsync();

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

        _logger.LogInformation("User {UserId} updated their profile", user.Id);

        TempData["SuccessMessage"] = _localizer["Profile_Updated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
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

    private static (byte[] Data, string ContentType) ResizeProfilePicture(byte[] imageData)
    {
        using var original = SKBitmap.Decode(imageData);
        if (original == null)
        {
            throw new InvalidOperationException("Could not decode the uploaded image.");
        }

        var width = original.Width;
        var height = original.Height;
        var longSide = Math.Max(width, height);

        if (longSide > MaxProfilePictureLongSide)
        {
            var scale = (float)MaxProfilePictureLongSide / longSide;
            width = (int)(width * scale);
            height = (int)(height * scale);

            using var resized = original.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return (encoded.ToArray(), "image/jpeg");
        }

        // Image is already small enough — re-encode as JPEG for consistent storage
        using var smallImage = SKImage.FromBitmap(original);
        using var smallEncoded = smallImage.Encode(SKEncodedImageFormat.Jpeg, 85);
        return (smallEncoded.ToArray(), "image/jpeg");
    }

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
                verificationUrl!);

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

        ViewData["AdminEmail"] = _configuration["Email:AdminAddress"];
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

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}",
            user.Id, deletionDate);

        // Send confirmation email - load UserEmails for GetEffectiveEmail()
        await _dbContext.Entry(user).Collection(u => u.UserEmails).LoadAsync();
        var effectiveEmail = user.GetEffectiveEmail();
        if (effectiveEmail != null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                effectiveEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
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
                profile.IsSuspended,
                profile.EmergencyContactName,
                profile.EmergencyContactPhone,
                profile.EmergencyContactRelationship,
                HasCustomProfilePicture = profile.HasCustomProfilePicture,
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
                a.Motivation,
                a.AdditionalInfo,
                SubmittedAt = a.SubmittedAt.ToString(null, CultureInfo.InvariantCulture),
                ResolvedAt = a.ResolvedAt?.ToString(null, CultureInfo.InvariantCulture)
            }),
            Consents = consents.Select(c => new
            {
                DocumentName = c.DocumentVersion?.LegalDocument?.Name,
                DocumentVersion = c.DocumentVersion?.VersionNumber,
                c.ExplicitConsent,
                ConsentedAt = c.ConsentedAt.ToString(null, CultureInfo.InvariantCulture),
                c.IpAddress,
                c.UserAgent
            }),
            TeamMemberships = teamMemberships.Select(tm => new
            {
                TeamName = tm.Team?.Name,
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

        return Json(export, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"nobodies-profiles-export-{DateTime.UtcNow:yyyy-MM-dd}.json";

        return File(bytes, "application/json", fileName);
    }
}
