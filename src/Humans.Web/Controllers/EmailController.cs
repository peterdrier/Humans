using Humans.Application.Architecture;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Email")]
public class EmailController(
    IUserServiceRead userService,
    IEmailOutboxService outboxService,
    ILogger<EmailController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpGet("EmailOutbox")]
    public async Task<IActionResult> EmailOutbox()
    {
        var stats = await outboxService.GetOutboxStatsAsync();

        var viewModel = new EmailOutboxViewModel
        {
            TotalMessageCount = stats.TotalCount,
            QueuedCount = stats.QueuedCount,
            SentLast24HoursCount = stats.SentLast24HoursCount,
            FailedCount = stats.FailedCount,
            IsPaused = stats.IsPaused,
            Messages = stats.RecentMessages.ToList(),
        };

        return View(viewModel);
    }

    [HttpPost("EmailOutbox/Pause")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PauseEmailSending()
    {
        await outboxService.SetEmailPausedAsync(true);
        logger.LogInformation("Admin {AdminId} paused email sending", User.Identity?.Name);
        SetSuccess("Email sending paused.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Resume")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumeEmailSending()
    {
        await outboxService.SetEmailPausedAsync(false);
        logger.LogInformation("Admin {AdminId} resumed email sending", User.Identity?.Name);
        SetSuccess("Email sending resumed.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Retry/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryEmailOutboxMessage(Guid id)
    {
        var recipient = await outboxService.RetryMessageAsync(id);
        if (recipient is null) return NotFound();

        SetSuccess($"Message to {recipient} queued for retry.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    [HttpPost("EmailOutbox/Discard/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardEmailOutboxMessage(Guid id)
    {
        var recipient = await outboxService.DiscardMessageAsync(id);
        if (recipient is null) return NotFound();

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

        var personas = new Dictionary<string, (string Name, string Email)>(StringComparer.Ordinal)
        {
            ["en"] = ("Sally Smith", "sally@example.com"),
            ["es"] = ("María García", "maria@example.com"),
            ["de"] = ("Frieda Fischer", "frieda@example.com"),
            ["fr"] = ("François Dupont", "francois@example.com"),
            ["it"] = ("Giulia Rossi", "giulia@example.com"),
            ["ca"] = ("Jordi Puig", "jordi@example.com"),
        };

        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in cultures)
        {
            var (name, email) = personas[culture];
            previews[culture] = RenderSamples(renderer, settings, name, email, culture)
                .Select(s => new EmailPreviewItem
                {
                    Id = s.Id, Name = s.Name, Recipient = s.Recipient,
                    Subject = s.Content.Subject, Body = s.Content.HtmlBody
                })
                .ToList();
        }

        return View(new EmailPreviewViewModel { Previews = previews, FromAddress = settings.FromAddress });
    }

    // One sample per template the renderer exposes — the preview gallery's catalog.
    private static IEnumerable<(string Id, string Name, string Recipient, EmailContent Content)> RenderSamples(
        IEmailRenderer renderer, EmailSettings settings, string name, string email, string culture)
    {
        var sampleDocs = new[] { "Volunteer Agreement", "Privacy Policy" };
        var sampleResources = new (string Name, string? Url)[]
        {
            ("Art Collective Shared Drive", "https://drive.google.com/drive/folders/example"),
            ("art-collective@nobodies.team", "https://groups.google.com/g/art-collective"),
        };
        const string sampleMessage = "Hi! I'm organizing the next community event and would love your help. Let me know if you're interested!";
        var verifyUrl = $"{settings.BaseUrl}/Profile/VerifyEmail?token=sample-token";

        yield return ("application-submitted", "Application Submitted (to Admin)", settings.AdminAddress, renderer.RenderApplicationSubmitted(Guid.Empty, name));
        yield return ("application-approved", "Application Approved", email, renderer.RenderApplicationApproved(name, MembershipTier.Colaborador, culture));
        yield return ("application-rejected", "Application Rejected", email, renderer.RenderApplicationRejected(name, MembershipTier.Asociado, "Incomplete profile information", culture));
        yield return ("signup-rejected", "Signup Rejected", email, renderer.RenderSignupRejected(name, "Incomplete profile information", culture));
        yield return ("reconsent-required", "Re-Consent Required (single doc)", email, renderer.RenderReConsentsRequired(name, [sampleDocs[0]], culture));
        yield return ("reconsents-required", "Re-Consents Required (multiple docs)", email, renderer.RenderReConsentsRequired(name, sampleDocs, culture));
        yield return ("reconsent-reminder", "Re-Consent Reminder", email, renderer.RenderReConsentReminder(name, sampleDocs, 14, culture));
        yield return ("welcome", "Welcome", email, renderer.RenderWelcome(name, culture));
        yield return ("access-suspended", "Access Suspended", email, renderer.RenderAccessSuspended(name, "Outstanding consent requirements", culture));
        yield return ("email-verification", "Email Verification", "newemail@example.com", renderer.RenderEmailVerification(name, "newemail@example.com", verifyUrl, culture: culture));
        yield return ("email-verification-merge", "Email Verification (Merge)", "duplicate@example.com", renderer.RenderEmailVerification(name, "duplicate@example.com", verifyUrl, isConflict: true, culture: culture));
        yield return ("deletion-requested", "Account Deletion Requested", email, renderer.RenderAccountDeletionRequested(name, "March 15, 2026", culture));
        yield return ("account-deleted", "Account Deleted", email, renderer.RenderAccountDeleted(name, culture));
        yield return ("added-to-team", "Added to Team", email, renderer.RenderAddedToTeam(name, "Art Collective", "art-collective", sampleResources, culture));
        yield return ("term-renewal-reminder", "Term Renewal Reminder", email, renderer.RenderTermRenewalReminder(name, "Colaborador", "April 1, 2026", culture));
        yield return ("facilitated-message", "Facilitated Message (with contact info)", email, renderer.RenderFacilitatedMessage(name, "Alex Firestone", sampleMessage, true, "alex@example.com", culture));
        yield return ("facilitated-message-anon", "Facilitated Message (without contact info)", email, renderer.RenderFacilitatedMessage(name, "Alex Firestone", sampleMessage, false, null, culture));
        yield return ("google-group-removal-loss", "Google Group Removal — Loss of Access", email, renderer.RenderGoogleGroupRemovalLossOfAccess(name, "Art Collective", "art-collective@nobodies.team", culture));
        yield return ("google-drive-removal-loss", "Google Drive Removal — Loss of Access", email, renderer.RenderGoogleDriveRemovalLossOfAccess(name, "Art Collective Shared Drive", culture));
        yield return ("google-removal-secondary-cleanup", "Google Access Removal — Secondary Email Cleanup", "old-" + email, renderer.RenderGoogleAccessRemovalSecondaryCleanup(name, "old-" + email, email, culture));
    }
}
