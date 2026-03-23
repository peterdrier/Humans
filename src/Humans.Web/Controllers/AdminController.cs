using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions;
using Microsoft.Extensions.Options;
using Humans.Web.Models;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin")]
public class AdminController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<AdminController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;
    private readonly IOnboardingService _onboardingService;

    public AdminController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<AdminController> logger,
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment,
        IOnboardingService onboardingService)
        : base(userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;
        _localizer = localizer;
        _environment = environment;
        _onboardingService = onboardingService;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpPost("Humans/{id}/Purge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (_environment.IsProduction())
        {
            return NotFound();
        }

        var user = await FindUserByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserAsync();

        if (user.Id == currentUser?.Id)
        {
            SetError("You cannot purge your own account.");
            return RedirectToAction("HumanDetail", "Human", new { id });
        }

        var displayName = user.DisplayName;

        _logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, _environment.EnvironmentName);

        // Sever OAuth link so next Google login creates a fresh user
        var logins = await _userManager.GetLoginsAsync(user);
        foreach (var login in logins)
        {
            await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        }

        var result = await _onboardingService.PurgeHumanAsync(id);
        if (!result.Success)
        {
            return NotFound();
        }

        SetSuccess($"Purged {displayName}. They will get a fresh account on next login.");
        return RedirectToAction("Humans", "Human");
    }

    [HttpPost("SyncSystemTeams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSystemTeams(
        [FromServices] Humans.Infrastructure.Jobs.SystemTeamSyncJob systemTeamSyncJob)
    {
        try
        {
            var report = await systemTeamSyncJob.ExecuteAsync();
            TempData["SyncReport"] = System.Text.Json.JsonSerializer.Serialize(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync system teams");
            SetError($"Sync failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(SyncResults));
    }

    [HttpGet("SyncResults")]
    public IActionResult SyncResults()
    {
        SyncReport? report = null;
        if (TempData["SyncReport"] is string json)
        {
            report = System.Text.Json.JsonSerializer.Deserialize<SyncReport>(json);
        }

        if (report is null)
        {
            SetInfo("No sync results to display. Run a sync first.");
            return RedirectToAction(nameof(Index));
        }

        return View(report);
    }

    [HttpGet("Logs")]
    public IActionResult Logs(int count = 50)
    {
        count = Math.Clamp(count, 1, 200);
        var events = Web.Infrastructure.InMemoryLogSink.Instance.GetEvents(count);
        return View(events);
    }

    [HttpGet("Configuration")]
    public IActionResult Configuration([FromServices] IConfiguration configuration)
    {
        var keys = new (string Section, string Key, bool Required)[]
        {
            ("Authentication", "Authentication:Google:ClientId", true),
            ("Authentication", "Authentication:Google:ClientSecret", true),
            ("Database", "ConnectionStrings:DefaultConnection", true),
            ("Email", "Email:SmtpHost", true),
            ("Email", "Email:Username", true),
            ("Email", "Email:Password", true),
            ("Email", "Email:FromAddress", true),
            ("Email", "Email:BaseUrl", true),
            ("GitHub", "GitHub:Owner", true),
            ("GitHub", "GitHub:Repository", true),
            ("GitHub", "GitHub:AccessToken", true),
            ("Google Maps", "GoogleMaps:ApiKey", true),
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyPath", false),
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyJson", false),
            ("Google Workspace", "GoogleWorkspace:Domain", false),
            ("OpenTelemetry", "OpenTelemetry:OtlpEndpoint", false),
            ("Ticket Vendor", "TicketVendor:EventId", false),
            ("Ticket Vendor", "TicketVendor:Provider", false),
            ("Ticket Vendor", "TicketVendor:SyncIntervalMinutes", false),
        };

        var items = keys.Select(k =>
        {
            var value = configuration[k.Key];
            var isSet = !string.IsNullOrEmpty(value);
            string preview = "(not set)";
            if (isSet)
            {
                preview = value![..Math.Min(3, value!.Length)] + "...";
            }

            return new ConfigurationItemViewModel
            {
                Section = k.Section,
                Key = k.Key,
                IsSet = isSet,
                Preview = preview,
                IsRequired = k.Required,
            };
        }).ToList();

        // Env var keys (inserted in alphabetical order by section)
        var feedbackApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY");
        items.Insert(
            items.FindIndex(i => string.Equals(i.Section, "GitHub", StringComparison.Ordinal)),
            new ConfigurationItemViewModel
            {
                Section = "Feedback API",
                Key = "FEEDBACK_API_KEY (env)",
                IsSet = !string.IsNullOrEmpty(feedbackApiKey),
                Preview = !string.IsNullOrEmpty(feedbackApiKey) ? feedbackApiKey[..Math.Min(3, feedbackApiKey.Length)] + "..." : "(not set)",
                IsRequired = false,
            });

        var apiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY");
        items.Add(new ConfigurationItemViewModel
        {
            Section = "Ticket Vendor",
            Key = "TICKET_VENDOR_API_KEY (env)",
            IsSet = !string.IsNullOrEmpty(apiKey),
            Preview = !string.IsNullOrEmpty(apiKey) ? apiKey[..Math.Min(3, apiKey.Length)] + "..." : "(not set)",
            IsRequired = false,
        });

        return View(new AdminConfigurationViewModel { Items = items });
    }

    [HttpGet("EmailPreview")]
    public IActionResult EmailPreview(
        [FromServices] IEmailRenderer renderer,
        [FromServices] IOptions<EmailSettings> emailSettings)
    {
        var settings = emailSettings.Value;
        var cultures = new[] { "en", "es", "de", "fr", "it" };

        // Per-locale persona stubs for realistic previews
        var personas = new Dictionary<string, (string Name, string Email)>(StringComparer.Ordinal)
        {
            ["en"] = ("Sally Smith", "sally@example.com"),
            ["es"] = ("Mar\u00eda Garc\u00eda", "maria@example.com"),
            ["de"] = ("Frieda Fischer", "frieda@example.com"),
            ["fr"] = ("Fran\u00e7ois Dupont", "francois@example.com"),
            ["it"] = ("Giulia Rossi", "giulia@example.com"),
        };

        var sampleDocs = new[] { "Volunteer Agreement", "Privacy Policy" };
        var sampleResources = new (string Name, string? Url)[]
        {
            ("Art Collective Shared Drive", "https://drive.google.com/drive/folders/example"),
            ("art-collective@nobodies.team", "https://groups.google.com/g/art-collective"),
        };

        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in cultures)
        {
            var (name, email) = personas[culture];

            var items = new List<EmailPreviewItem>();

            var c1 = renderer.RenderApplicationSubmitted(Guid.Empty, name);
            items.Add(new EmailPreviewItem { Id = "application-submitted", Name = "Application Submitted (to Admin)", Recipient = settings.AdminAddress, Subject = c1.Subject, Body = c1.HtmlBody });

            var c2 = renderer.RenderApplicationApproved(name, MembershipTier.Colaborador, culture);
            items.Add(new EmailPreviewItem { Id = "application-approved", Name = "Application Approved", Recipient = email, Subject = c2.Subject, Body = c2.HtmlBody });

            var c3 = renderer.RenderApplicationRejected(name, MembershipTier.Asociado, "Incomplete profile information", culture);
            items.Add(new EmailPreviewItem { Id = "application-rejected", Name = "Application Rejected", Recipient = email, Subject = c3.Subject, Body = c3.HtmlBody });

            var c4 = renderer.RenderSignupRejected(name, "Incomplete profile information", culture);
            items.Add(new EmailPreviewItem { Id = "signup-rejected", Name = "Signup Rejected", Recipient = email, Subject = c4.Subject, Body = c4.HtmlBody });

            var c5 = renderer.RenderReConsentsRequired(name, new[] { sampleDocs[0] }, culture);
            items.Add(new EmailPreviewItem { Id = "reconsent-required", Name = "Re-Consent Required (single doc)", Recipient = email, Subject = c5.Subject, Body = c5.HtmlBody });

            var c6 = renderer.RenderReConsentsRequired(name, sampleDocs, culture);
            items.Add(new EmailPreviewItem { Id = "reconsents-required", Name = "Re-Consents Required (multiple docs)", Recipient = email, Subject = c6.Subject, Body = c6.HtmlBody });

            var c7 = renderer.RenderReConsentReminder(name, sampleDocs, 14, culture);
            items.Add(new EmailPreviewItem { Id = "reconsent-reminder", Name = "Re-Consent Reminder", Recipient = email, Subject = c7.Subject, Body = c7.HtmlBody });

            var c8 = renderer.RenderWelcome(name, culture);
            items.Add(new EmailPreviewItem { Id = "welcome", Name = "Welcome", Recipient = email, Subject = c8.Subject, Body = c8.HtmlBody });

            var c9 = renderer.RenderAccessSuspended(name, "Outstanding consent requirements", culture);
            items.Add(new EmailPreviewItem { Id = "access-suspended", Name = "Access Suspended", Recipient = email, Subject = c9.Subject, Body = c9.HtmlBody });

            var c10 = renderer.RenderEmailVerification(name, "newemail@example.com", $"{settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", culture: culture);
            items.Add(new EmailPreviewItem { Id = "email-verification", Name = "Email Verification", Recipient = "newemail@example.com", Subject = c10.Subject, Body = c10.HtmlBody });

            var c10m = renderer.RenderEmailVerification(name, "duplicate@example.com", $"{settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", isConflict: true, culture: culture);
            items.Add(new EmailPreviewItem { Id = "email-verification-merge", Name = "Email Verification (Merge)", Recipient = "duplicate@example.com", Subject = c10m.Subject, Body = c10m.HtmlBody });

            var c11 = renderer.RenderAccountDeletionRequested(name, "March 15, 2026", culture);
            items.Add(new EmailPreviewItem { Id = "deletion-requested", Name = "Account Deletion Requested", Recipient = email, Subject = c11.Subject, Body = c11.HtmlBody });

            var c12 = renderer.RenderAccountDeleted(name, culture);
            items.Add(new EmailPreviewItem { Id = "account-deleted", Name = "Account Deleted", Recipient = email, Subject = c12.Subject, Body = c12.HtmlBody });

            var c13 = renderer.RenderAddedToTeam(name, "Art Collective", "art-collective", sampleResources, culture);
            items.Add(new EmailPreviewItem { Id = "added-to-team", Name = "Added to Team", Recipient = email, Subject = c13.Subject, Body = c13.HtmlBody });

            var c14 = renderer.RenderTermRenewalReminder(name, "Colaborador", "April 1, 2026", culture);
            items.Add(new EmailPreviewItem { Id = "term-renewal-reminder", Name = "Term Renewal Reminder", Recipient = email, Subject = c14.Subject, Body = c14.HtmlBody });

            var sampleDigestGroups = new List<BoardDigestTierGroup>
            {
                new("Volunteer", new[] { "Alice Johnson", "Bob Smith" }),
                new("Colaborador", new[] { "Carlos García" })
            };
            var sampleOutstanding = new BoardDigestOutstandingCounts(
                OnboardingReview: 3,
                StillOnboarding: 5,
                BoardVotingTotal: 7,
                BoardVotingYours: 4,
                TeamJoinRequests: 2,
                PendingConsents: 12,
                PendingDeletions: 1);
            var c15 = renderer.RenderBoardDailyDigest(name, "2026-02-22", sampleDigestGroups, sampleOutstanding, culture);
            items.Add(new EmailPreviewItem { Id = "board-daily-digest", Name = "Board Daily Digest", Recipient = email, Subject = c15.Subject, Body = c15.HtmlBody });

            var cMsg1 = renderer.RenderFacilitatedMessage(name, "Alex Firestone", "Hi! I'm organizing the next community event and would love your help. Let me know if you're interested!", true, "alex@example.com", culture);
            items.Add(new EmailPreviewItem { Id = "facilitated-message", Name = "Facilitated Message (with contact info)", Recipient = email, Subject = cMsg1.Subject, Body = cMsg1.HtmlBody });

            var cMsg2 = renderer.RenderFacilitatedMessage(name, "Alex Firestone", "Hi! I'm organizing the next community event and would love your help. Let me know if you're interested!", false, null, culture);
            items.Add(new EmailPreviewItem { Id = "facilitated-message-anon", Name = "Facilitated Message (without contact info)", Recipient = email, Subject = cMsg2.Subject, Body = cMsg2.HtmlBody });

            previews[culture] = items;
        }

        return View(new EmailPreviewViewModel { Previews = previews });
    }

    [HttpGet("SyncSettings")]
    public async Task<IActionResult> SyncSettings([FromServices] ISyncSettingsService syncSettingsService)
    {
        var settings = await syncSettingsService.GetAllAsync();
        var viewModel = new SyncSettingsViewModel
        {
            Settings = settings.Select(s => new SyncServiceSettingViewModel
            {
                ServiceType = s.ServiceType,
                ServiceName = FormatServiceName(s.ServiceType),
                CurrentMode = s.SyncMode,
                UpdatedAt = s.UpdatedAt.ToDateTimeUtc(),
                UpdatedByName = s.UpdatedByUser?.DisplayName
            }).ToList()
        };
        return View(viewModel);
    }

    [HttpPost("SyncSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSyncSetting(
        [FromServices] ISyncSettingsService syncSettingsService,
        SyncServiceType serviceType, SyncMode mode)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        await syncSettingsService.UpdateModeAsync(serviceType, mode, currentUser.Id);

        _logger.LogInformation("Admin {AdminId} changed {ServiceType} sync mode to {Mode}",
            currentUser.Id, serviceType, mode);

        SetSuccess($"Sync mode for {FormatServiceName(serviceType)} updated to {mode}.");
        return RedirectToAction(nameof(SyncSettings));
    }

    private static string FormatServiceName(SyncServiceType type) => type switch
    {
        SyncServiceType.GoogleDrive => "Google Drive",
        SyncServiceType.GoogleGroups => "Google Groups",
        SyncServiceType.Discord => "Discord",
        _ => type.ToString()
    };

    // Intentionally anonymous: exposes only migration names and counts (no sensitive data).
    // Used by dev tooling to check which migrations have been applied in QA/prod,
    // so old migrations can be safely squashed and removed from the repo.
    [HttpGet("DbVersion")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> DbVersion()
    {
        var applied = (await _dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = await _dbContext.Database.GetPendingMigrationsAsync();

        return Ok(new
        {
            lastApplied = applied.LastOrDefault(),
            appliedCount = applied.Count,
            pendingCount = pending.Count()
        });
    }

    [HttpGet("EmailOutbox")]
    public async Task<IActionResult> EmailOutbox([FromServices] IClock clock)
    {
        var now = clock.GetCurrentInstant();
        var cutoff24h = now - Duration.FromHours(24);

        var queuedCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Queued);
        var sentLast24H = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Sent && m.SentAt > cutoff24h);
        var failedCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Failed);

        var pausedSetting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "IsEmailSendingPaused");
        var isPaused = string.Equals(pausedSetting?.Value, "true", StringComparison.OrdinalIgnoreCase);

        var messages = await _dbContext.EmailOutboxMessages
            .Include(m => m.User)
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();

        var viewModel = new EmailOutboxViewModel
        {
            QueuedCount = queuedCount,
            SentLast24HoursCount = sentLast24H,
            FailedCount = failedCount,
            IsPaused = isPaused,
            Messages = messages,
        };

        return View(viewModel);
    }

    [HttpPost("EmailOutbox/Pause")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PauseEmailSending()
    {
        await SetEmailPausedAsync(true);
        _logger.LogInformation("Admin {AdminId} paused email sending", User.Identity?.Name);
        SetSuccess("Email sending paused.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Resume")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumeEmailSending()
    {
        await SetEmailPausedAsync(false);
        _logger.LogInformation("Admin {AdminId} resumed email sending", User.Identity?.Name);
        SetSuccess("Email sending resumed.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Retry/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryEmailOutboxMessage(Guid id)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync(id);
        if (message is null) return NotFound();

        message.Status = EmailOutboxStatus.Queued;
        message.RetryCount = 0;
        message.LastError = null;
        message.NextRetryAt = null;
        message.PickedUpAt = null;
        await _dbContext.SaveChangesAsync();

        SetSuccess($"Message to {message.RecipientEmail} queued for retry.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Discard/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardEmailOutboxMessage(Guid id)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync(id);
        if (message is null) return NotFound();

        var recipient = message.RecipientEmail;
        _dbContext.EmailOutboxMessages.Remove(message);
        await _dbContext.SaveChangesAsync();

        SetSuccess($"Message to {recipient} discarded.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    private async Task SetEmailPausedAsync(bool paused)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "IsEmailSendingPaused");
        if (setting is null)
        {
            setting = new SystemSetting { Key = "IsEmailSendingPaused", Value = paused ? "true" : "false" };
            _dbContext.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = paused ? "true" : "false";
        }
        await _dbContext.SaveChangesAsync();
    }

    [HttpPost("ClearHangfireLocks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearHangfireLocks()
    {
        var deleted = await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock");

        _logger.LogWarning("Admin cleared {Count} stale Hangfire locks", deleted);
        SetSuccess($"Cleared {deleted} Hangfire lock(s). Restart the app to re-register recurring jobs.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("CheckGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckGroupSettings(
        [FromServices] IGoogleSyncService googleSyncService)
    {
        try
        {
            var result = await googleSyncService.CheckGroupSettingsAsync();
            TempData["GroupSettingsResult"] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Google Group settings");
            SetError($"Settings check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(GroupSettingsResults));
    }

    [HttpGet("GroupSettingsResults")]
    public IActionResult GroupSettingsResults()
    {
        Application.DTOs.GroupSettingsDriftResult? result = null;
        if (TempData["GroupSettingsResult"] is string json)
        {
            result = System.Text.Json.JsonSerializer.Deserialize<Application.DTOs.GroupSettingsDriftResult>(json);
        }

        if (result is null)
        {
            SetInfo("No group settings results to display. Run the check first.");
            return RedirectToAction(nameof(Index));
        }

        return View(result);
    }

    [HttpPost("CheckEmailMismatches")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckEmailMismatches(
        [FromServices] IGoogleSyncService googleSyncService)
    {
        try
        {
            var result = await googleSyncService.GetEmailMismatchesAsync();
            TempData["EmailBackfillResult"] = System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check email mismatches");
            SetError($"Email mismatch check failed: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(EmailBackfillReview));
    }

    [HttpGet("EmailBackfillReview")]
    public IActionResult EmailBackfillReview()
    {
        Application.DTOs.EmailBackfillResult? result = null;
        if (TempData["EmailBackfillResult"] is string json)
        {
            result = System.Text.Json.JsonSerializer.Deserialize<Application.DTOs.EmailBackfillResult>(json);
        }

        if (result is null)
        {
            SetInfo("No email mismatch results to display. Run the check first.");
            return RedirectToAction(nameof(Index));
        }

        return View(result);
    }

    [HttpPost("ApplyEmailBackfill")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyEmailBackfill(
        [FromForm] List<Guid> selectedUserIds,
        [FromForm] Dictionary<string, string> corrections)
    {
        if (selectedUserIds.Count == 0)
        {
            SetInfo("No users selected.");
            return RedirectToAction(nameof(Index));
        }

        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null) return Unauthorized();

        var updated = 0;
        var errors = new List<string>();

        foreach (var userId in selectedUserIds)
        {
            if (!corrections.TryGetValue(userId.ToString(), out var googleEmail) || string.IsNullOrEmpty(googleEmail))
                continue;

            try
            {
                var user = await _dbContext.Users
                    .Include(u => u.UserEmails)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user is null)
                {
                    errors.Add($"User {userId} not found.");
                    continue;
                }

                var oldEmail = user.Email;

                user.Email = googleEmail;
                user.UserName = googleEmail;
                user.NormalizedEmail = googleEmail.ToUpperInvariant();
                user.NormalizedUserName = googleEmail.ToUpperInvariant();

                // Update OAuth UserEmail record if it exists
                var oauthEmail = user.UserEmails.FirstOrDefault(e => e.IsOAuth);
                if (oauthEmail is not null)
                {
                    oauthEmail.Email = googleEmail;
                }

                _logger.LogInformation(
                    "Admin {AdminId} applying email backfill for user {UserId}: '{OldEmail}' → '{NewEmail}'",
                    currentUser.Id, userId, oldEmail, googleEmail);

                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update email for user {UserId}", userId);
                errors.Add($"Error updating user {userId}: {ex.Message}");
            }
        }

        if (updated > 0)
            await _dbContext.SaveChangesAsync();

        if (errors.Count > 0)
            SetError($"Applied {updated} correction(s) with {errors.Count} error(s): {string.Join("; ", errors)}");
        else
            SetSuccess($"Applied {updated} email correction(s) successfully.");

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("AllGroups")]
    public async Task<IActionResult> AllGroups([FromServices] IGoogleSyncService googleSyncService)
    {
        try
        {
            var result = await googleSyncService.GetAllDomainGroupsAsync();
            ViewBag.Teams = await _dbContext.Teams.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
            return View(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load domain groups");
            SetError($"Failed to load domain groups: {ex.Message}");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("RemediateGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateGroupSettings(
        [FromServices] IGoogleSyncService googleSyncService,
        [FromForm] string groupEmail, [FromForm] string? returnUrl)
    {
        try
        {
            var success = await googleSyncService.RemediateGroupSettingsAsync(groupEmail);
            if (success) SetSuccess($"Settings remediated for {groupEmail}.");
            else SetError($"Remediation skipped for {groupEmail} — sync may be disabled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
            SetError($"Remediation failed for {groupEmail}: {ex.Message}");
        }
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(AllGroups))!);
    }

    [HttpPost("RemediateAllGroupSettings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemediateAllGroupSettings(
        [FromServices] IGoogleSyncService googleSyncService)
    {
        try
        {
            var result = await googleSyncService.GetAllDomainGroupsAsync();
            if (result.ErrorMessage is not null)
            {
                SetError($"Failed to enumerate groups: {result.ErrorMessage}");
                return RedirectToAction(nameof(AllGroups));
            }

            var drifted = result.Groups.Where(g => g.HasDrift).ToList();
            if (drifted.Count == 0)
            {
                SetInfo("No drifted groups found — nothing to remediate.");
                return RedirectToAction(nameof(AllGroups));
            }

            var fixed_ = 0;
            var errors = new List<string>();

            foreach (var group in drifted)
            {
                try
                {
                    var success = await googleSyncService.RemediateGroupSettingsAsync(group.GroupEmail);
                    if (success) fixed_++;
                    else errors.Add($"{group.GroupEmail}: sync disabled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remediate {GroupEmail}", group.GroupEmail);
                    errors.Add($"{group.GroupEmail}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
                SetError($"Remediated {fixed_} group(s) with {errors.Count} error(s): {string.Join("; ", errors)}");
            else
                SetSuccess($"Remediated {fixed_} drifted group(s) successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate all group settings");
            SetError($"Batch remediation failed: {ex.Message}");
        }
        return RedirectToAction(nameof(AllGroups));
    }

    [HttpPost("LinkGroupToTeam")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroupToTeam(
        [FromServices] IGoogleSyncService googleSyncService,
        [FromForm] Guid teamId, [FromForm] string groupPrefix)
    {
        if (string.IsNullOrWhiteSpace(groupPrefix))
        {
            SetError("Group prefix is required.");
            return RedirectToAction(nameof(AllGroups));
        }

        try
        {
            var team = await _dbContext.Teams.FindAsync(teamId);
            if (team is null) return NotFound();

            team.GoogleGroupPrefix = groupPrefix.Trim().ToLowerInvariant();
            await _dbContext.SaveChangesAsync();

            var linkResult = await googleSyncService.EnsureTeamGroupAsync(teamId);
            if (linkResult.RequiresConfirmation)
                SetInfo($"Linked group for team \"{team.Name}\". Note: {linkResult.WarningMessage}");
            else if (linkResult.ErrorMessage is not null)
                SetError($"Could not link group: {linkResult.ErrorMessage}");
            else
                SetSuccess($"Successfully linked {groupPrefix}@nobodies.team to team \"{team.Name}\".");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            SetError($"Failed to link group: {ex.Message}");
        }

        return RedirectToAction(nameof(AllGroups));
    }

}
