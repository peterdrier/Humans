using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Web.Extensions;
using Humans.Web.Models;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Email")]
public class EmailController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<EmailController> _logger;

    public EmailController(
        UserManager<User> userManager,
        HumansDbContext dbContext,
        ILogger<EmailController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpGet("EmailOutbox")]
    public async Task<IActionResult> EmailOutbox([FromServices] IClock clock)
    {
        var now = clock.GetCurrentInstant();
        var cutoff24h = now - Duration.FromHours(24);

        var totalCount = await _dbContext.EmailOutboxMessages.CountAsync();
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
            TotalMessageCount = totalCount,
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

    [HttpGet("EmailPreview")]
    public IActionResult EmailPreview(
        [FromServices] IEmailRenderer renderer,
        [FromServices] IOptions<EmailSettings> emailSettings)
    {
        var settings = emailSettings.Value;
        var cultures = new[] { "en", "es", "de", "fr", "it", "ca" };

        // Per-locale persona stubs for realistic previews
        var personas = new Dictionary<string, (string Name, string Email)>(StringComparer.Ordinal)
        {
            ["en"] = ("Sally Smith", "sally@example.com"),
            ["es"] = ("Mar\u00eda Garc\u00eda", "maria@example.com"),
            ["de"] = ("Frieda Fischer", "frieda@example.com"),
            ["fr"] = ("Fran\u00e7ois Dupont", "francois@example.com"),
            ["it"] = ("Giulia Rossi", "giulia@example.com"),
            ["ca"] = ("Jordi Puig", "jordi@example.com"),
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
                new("Colaborador", new[] { "Carlos Garc\u00eda" })
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

        return View(new EmailPreviewViewModel { Previews = previews, FromAddress = settings.FromAddress });
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
}
