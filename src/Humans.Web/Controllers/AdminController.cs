using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
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

[Authorize(Roles = "Admin")]
[Route("Admin")]
public class AdminController : Controller
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

        var user = await _userManager.FindByIdAsync(id.ToString());
        if (user == null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);

        if (user.Id == currentUser?.Id)
        {
            TempData["ErrorMessage"] = "You cannot purge your own account.";
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

        TempData["SuccessMessage"] = $"Purged {displayName}. They will get a fresh account on next login.";
        return RedirectToAction("Humans", "Human");
    }

    [HttpPost("SyncSystemTeams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSystemTeams(
        [FromServices] Infrastructure.Jobs.SystemTeamSyncJob systemTeamSyncJob)
    {
        try
        {
            await systemTeamSyncJob.ExecuteAsync();
            TempData["SuccessMessage"] = "System teams synced successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync system teams");
            TempData["ErrorMessage"] = $"Sync failed: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
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
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyPath", false),
            ("Google Workspace", "GoogleWorkspace:ServiceAccountKeyJson", false),
            ("Google Workspace", "GoogleWorkspace:Domain", false),
            ("GitHub", "GitHub:Owner", true),
            ("GitHub", "GitHub:Repository", true),
            ("GitHub", "GitHub:AccessToken", true),
            ("Google Maps", "GoogleMaps:ApiKey", true),
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

        // Ticket vendor API key is from env var, not configuration
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

            var c10 = renderer.RenderEmailVerification(name, "preferred@example.com", $"{settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", culture);
            items.Add(new EmailPreviewItem { Id = "email-verification", Name = "Email Verification", Recipient = "preferred@example.com", Subject = c10.Subject, Body = c10.HtmlBody });

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
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        await syncSettingsService.UpdateModeAsync(serviceType, mode, currentUser.Id);

        _logger.LogInformation("Admin {AdminId} changed {ServiceType} sync mode to {Mode}",
            currentUser.Id, serviceType, mode);

        TempData["SuccessMessage"] = $"Sync mode for {FormatServiceName(serviceType)} updated to {mode}.";
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
        TempData["SuccessMessage"] = "Email sending paused.";
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Resume")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumeEmailSending()
    {
        await SetEmailPausedAsync(false);
        _logger.LogInformation("Admin {AdminId} resumed email sending", User.Identity?.Name);
        TempData["SuccessMessage"] = "Email sending resumed.";
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Retry/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryEmailOutboxMessage(Guid id)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync(id);
        if (message == null) return NotFound();

        message.Status = EmailOutboxStatus.Queued;
        message.RetryCount = 0;
        message.LastError = null;
        message.NextRetryAt = null;
        message.PickedUpAt = null;
        await _dbContext.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Message to {message.RecipientEmail} queued for retry.";
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Discard/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardEmailOutboxMessage(Guid id)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync(id);
        if (message == null) return NotFound();

        var recipient = message.RecipientEmail;
        _dbContext.EmailOutboxMessages.Remove(message);
        await _dbContext.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Message to {recipient} discarded.";
        return RedirectToAction(nameof(EmailOutbox));
    }

    private async Task SetEmailPausedAsync(bool paused)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "IsEmailSendingPaused");
        if (setting == null)
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

}
