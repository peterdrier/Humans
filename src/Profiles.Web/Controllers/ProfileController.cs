using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Application.DTOs;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;
using Profiles.Web.Models;

namespace Profiles.Web.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly ProfilesDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<ProfileController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IContactFieldService _contactFieldService;
    private readonly IVolunteerHistoryService _volunteerHistoryService;
    private readonly IEmailService _emailService;
    private readonly ITeamService _teamService;
    private readonly IMembershipCalculator _membershipCalculator;

    private const string EmailVerificationTokenPurpose = "PreferredEmailVerification";
    private const int VerificationCooldownMinutes = 5;

    public ProfileController(
        ProfilesDbContext dbContext,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ProfileController> logger,
        IConfiguration configuration,
        IContactFieldService contactFieldService,
        IVolunteerHistoryService volunteerHistoryService,
        IEmailService emailService,
        ITeamService teamService,
        IMembershipCalculator membershipCalculator)
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

        // Get consent status using canonical membership calculator logic
        var missingConsentVersions = await _membershipCalculator.GetMissingConsentVersionsAsync(user.Id);
        var pendingConsents = missingConsentVersions.Count;

        // Get contact fields (user viewing their own profile sees all)
        var contactFields = profile != null
            ? await _contactFieldService.GetVisibleContactFieldsAsync(profile.Id, user.Id)
            : [];

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
                IsMetalead = tm.Role == TeamMemberRole.Metalead,
                IsSystemTeam = tm.Team.IsSystemTeam
            })
            .ToList();

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            BurnerName = profile?.BurnerName ?? string.Empty,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            PhoneCountryCode = profile?.PhoneCountryCode,
            PhoneNumber = profile?.PhoneNumber,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Bio = profile?.Bio,
            HasPendingConsents = pendingConsents > 0,
            PendingConsentCount = pendingConsents,
            IsApproved = profile?.IsApproved ?? false,
            MembershipStatus = (await _membershipCalculator.ComputeStatusAsync(user.Id)).ToString(),
            CanViewLegalName = true, // User viewing their own profile
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

        var viewModel = new ProfileViewModel
        {
            Id = profile?.Id ?? Guid.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            BurnerName = profile?.BurnerName ?? user.DisplayName,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
            PhoneCountryCode = profile?.PhoneCountryCode,
            PhoneNumber = profile?.PhoneNumber,
            City = profile?.City,
            CountryCode = profile?.CountryCode,
            Latitude = profile?.Latitude,
            Longitude = profile?.Longitude,
            PlaceId = profile?.PlaceId,
            Bio = profile?.Bio,
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
        profile.PhoneCountryCode = model.PhoneCountryCode;
        profile.PhoneNumber = model.PhoneNumber;
        profile.City = model.City;
        profile.CountryCode = model.CountryCode;
        profile.Latitude = model.Latitude;
        profile.Longitude = model.Longitude;
        profile.PlaceId = model.PlaceId;
        profile.Bio = model.Bio;
        profile.UpdatedAt = now;

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

        await _contactFieldService.SaveContactFieldsAsync(profile.Id, contactFieldDtos);

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

        TempData["SuccessMessage"] = "Profile updated successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> PreferredEmail()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var viewModel = BuildPreferredEmailViewModel(user);
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPreferredEmail(PreferredEmailViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(model.NewEmail))
        {
            ModelState.AddModelError(nameof(model.NewEmail), "Please enter an email address.");
            return View(nameof(PreferredEmail), BuildPreferredEmailViewModel(user));
        }

        var newEmail = model.NewEmail.Trim();

        // Check if same as OAuth email
        if (string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.NewEmail),
                "This is already your sign-in email. No need to set it as preferred.");
            return View(nameof(PreferredEmail), BuildPreferredEmailViewModel(user));
        }

        // Check rate limit (5 minute cooldown)
        var now = _clock.GetCurrentInstant();
        if (user.PreferredEmailVerificationSentAt.HasValue)
        {
            var cooldownEnd = user.PreferredEmailVerificationSentAt.Value.Plus(Duration.FromMinutes(VerificationCooldownMinutes));
            if (now < cooldownEnd)
            {
                var minutesRemaining = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                ModelState.AddModelError(nameof(model.NewEmail),
                    $"Please wait {minutesRemaining} minute(s) before requesting another verification email.");
                return View(nameof(PreferredEmail), BuildPreferredEmailViewModel(user));
            }
        }

        // Check uniqueness among verified preferred emails (case-insensitive)
        var emailInUse = await _dbContext.Users
            .AnyAsync(u => u.Id != user.Id
                && u.PreferredEmailVerified
                && u.PreferredEmail != null
                && EF.Functions.ILike(u.PreferredEmail, newEmail));

        if (emailInUse)
        {
            ModelState.AddModelError(nameof(model.NewEmail),
                "This email address is already in use by another account.");
            return View(nameof(PreferredEmail), BuildPreferredEmailViewModel(user));
        }

        // Generate verification token
        var token = await _userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            EmailVerificationTokenPurpose);

        // Update user with pending email
        user.PreferredEmail = newEmail;
        user.PreferredEmailVerified = false;
        user.PreferredEmailVerificationSentAt = now;
        await _userManager.UpdateAsync(user);

        // Build verification URL
        var verificationUrl = Url.Action(
            nameof(VerifyEmail),
            "Profile",
            new { userId = user.Id, token = HttpUtility.UrlEncode(token) },
            Request.Scheme);

        // Send verification email
        await _emailService.SendEmailVerificationAsync(
            newEmail,
            user.DisplayName,
            verificationUrl!);

        _logger.LogInformation(
            "Sent preferred email verification to {Email} for user {UserId}",
            newEmail, user.Id);

        TempData["SuccessMessage"] = $"Verification email sent to {newEmail}. Please check your inbox.";
        return RedirectToAction(nameof(PreferredEmail));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(Guid userId, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return VerifyEmailError("Invalid verification link.");
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return VerifyEmailError("Invalid verification link.");
        }

        if (string.IsNullOrEmpty(user.PreferredEmail))
        {
            return VerifyEmailError("No email pending verification.");
        }

        // Verify the token
        var decodedToken = HttpUtility.UrlDecode(token);
        var isValid = await _userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            EmailVerificationTokenPurpose,
            decodedToken);

        if (!isValid)
        {
            return VerifyEmailError("The verification link is invalid or has expired.");
        }

        // Re-check uniqueness (guard against race conditions, case-insensitive)
        var emailInUse = await _dbContext.Users
            .AnyAsync(u => u.Id != user.Id
                && u.PreferredEmailVerified
                && u.PreferredEmail != null
                && EF.Functions.ILike(u.PreferredEmail, user.PreferredEmail));

        if (emailInUse)
        {
            user.PreferredEmail = null;
            user.PreferredEmailVerified = false;
            await _userManager.UpdateAsync(user);
            return VerifyEmailError("This email address has been claimed by another account.");
        }

        // Mark as verified
        user.PreferredEmailVerified = true;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation(
            "User {UserId} verified preferred email {Email}",
            user.Id, user.PreferredEmail);

        ViewData["Success"] = true;
        ViewData["Message"] = $"Email address {user.PreferredEmail} has been verified.";
        return View("VerifyEmailResult");
    }

    private IActionResult VerifyEmailError(string message)
    {
        ViewData["Success"] = false;
        ViewData["Message"] = message;
        return View("VerifyEmailResult");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPreferredEmail()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var previousEmail = user.PreferredEmail;
        user.PreferredEmail = null;
        user.PreferredEmailVerified = false;
        user.PreferredEmailVerificationSentAt = null;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation(
            "User {UserId} cleared preferred email (was: {Email})",
            user.Id, previousEmail);

        TempData["SuccessMessage"] = "Preferred email has been removed. System emails will now be sent to your sign-in email.";
        return RedirectToAction(nameof(PreferredEmail));
    }

    private PreferredEmailViewModel BuildPreferredEmailViewModel(User user)
    {
        var now = _clock.GetCurrentInstant();
        var canResend = true;
        var minutesUntilResend = 0;

        if (user.PreferredEmailVerificationSentAt.HasValue)
        {
            var cooldownEnd = user.PreferredEmailVerificationSentAt.Value.Plus(Duration.FromMinutes(VerificationCooldownMinutes));
            if (now < cooldownEnd)
            {
                canResend = false;
                minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
            }
        }

        var isPending = !string.IsNullOrEmpty(user.PreferredEmail) && !user.PreferredEmailVerified;

        return new PreferredEmailViewModel
        {
            OAuthEmail = user.Email ?? string.Empty,
            CurrentPreferredEmail = user.PreferredEmail,
            IsVerified = user.PreferredEmailVerified,
            IsPendingVerification = isPending,
            CanResendVerification = canResend,
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
            TempData["ErrorMessage"] = "A deletion request is already pending.";
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

        // Send confirmation email
        var effectiveEmail = user.GetEffectiveEmail();
        if (effectiveEmail != null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                effectiveEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
                CancellationToken.None);
        }

        TempData["SuccessMessage"] = $"Account deletion requested. Your account will be deleted on {deletionDate.ToDateTimeUtc():MMMM d, yyyy} unless you cancel.";
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
            TempData["ErrorMessage"] = "No deletion request is pending.";
            return RedirectToAction(nameof(Privacy));
        }

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("User {UserId} cancelled account deletion request", user.Id);

        TempData["SuccessMessage"] = "Account deletion has been cancelled.";
        return RedirectToAction(nameof(Privacy));
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> ExportData()
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

        var export = new
        {
            ExportedAt = _clock.GetCurrentInstant().ToString(null, CultureInfo.InvariantCulture),
            Account = new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                user.PreferredEmail,
                user.PreferredEmailVerified,
                CreatedAt = user.CreatedAt.ToString(null, CultureInfo.InvariantCulture),
                LastLoginAt = user.LastLoginAt?.ToString(null, CultureInfo.InvariantCulture)
            },
            Profile = profile != null ? new
            {
                profile.BurnerName,
                profile.FirstName,
                profile.LastName,
                profile.PhoneCountryCode,
                profile.PhoneNumber,
                profile.City,
                profile.CountryCode,
                profile.Bio,
                profile.IsSuspended,
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

        var fileName = $"profiles-data-export-{DateTime.UtcNow:yyyy-MM-dd}.json";
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
