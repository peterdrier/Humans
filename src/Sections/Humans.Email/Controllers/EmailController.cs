using Humans.Email.Contracts;
using Humans.Email.Services;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Email.Domain;
using Humans.Email.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Humans.Users.Contracts;

namespace Humans.Email.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Email")]
internal sealed class EmailController(
    IUserServiceRead userService,
    IEmailOutboxService outboxService,
    IAuditLogService audit,
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
        var dailyCounts = await outboxService.GetDailySendCountsAsync();

        var viewModel = new EmailOutboxViewModel
        {
            TotalMessageCount = stats.TotalCount,
            QueuedCount = stats.QueuedCount,
            SentLast24HoursCount = stats.SentLast24HoursCount,
            FailedCount = stats.FailedCount,
            IsPaused = stats.IsPaused,
            Messages = stats.RecentMessages.ToList(),
            DailyCounts = dailyCounts.ByDay.ToList(),
            TopTemplates = dailyCounts.TopTemplates.ToList(),
        };

        return View(viewModel);
    }

    [HttpGet("EmailOutbox/BackfillDailyCounts")]
    public async Task<IActionResult> BackfillDailyCountsPreview()
    {
        var preview = await outboxService.PreviewDailySendCountBackfillAsync();
        return View(new BackfillDailyCountsViewModel
        {
            RowsToAdd = preview.RowsToAdd,
            EarliestDate = preview.EarliestDate,
            LatestDate = preview.LatestDate,
            Sample = preview.Sample.ToList(),
        });
    }

    [HttpPost("EmailOutbox/BackfillDailyCounts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillDailyCounts()
    {
        var added = await outboxService.BackfillDailySendCountsAsync();
        logger.LogInformation("Admin {AdminId} backfilled {Count} daily send count row(s)", User.Identity?.Name, added);

        var actorId = GetCurrentUserId();
        if (actorId.HasValue)
        {
            await audit.LogAsync(
                AuditAction.EmailDailySendCountsBackfilled, nameof(EmailDailySendCount), Guid.Empty,
                $"Backfilled {added} daily send count row(s) from outbox history", actorId.Value);
        }

        SetSuccess($"Backfilled {added} daily send count row(s) from outbox history.");
        return RedirectToAction(nameof(EmailOutbox));
    }

    /// <summary>Posted from the /Settings#email tab (peterdrier/Humans#1634).</summary>
    [HttpPost("EmailOutbox/Pause")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PauseEmailSending()
    {
        await outboxService.SetEmailPausedAsync(true);
        logger.LogInformation("Admin {AdminId} paused email sending", User.Identity?.Name);
        SetSuccess("Email sending paused.");
        return Redirect("/Settings#email");
    }

    /// <summary>Posted from the /Settings#email tab (peterdrier/Humans#1634).</summary>
    [HttpPost("EmailOutbox/Resume")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumeEmailSending()
    {
        await outboxService.SetEmailPausedAsync(false);
        logger.LogInformation("Admin {AdminId} resumed email sending", User.Identity?.Name);
        SetSuccess("Email sending resumed.");
        return Redirect("/Settings#email");
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

    // Sample data + the (id, name, recipient, renderer-call) table below are pure
    // data — moved out of the method body so EmailPreview itself is just "loop
    // cultures, project the table, return the view" (was 51 statements / cc 2:
    // twenty near-identical var+Add pairs repeated per culture).
    private static readonly string[] Cultures = ["en", "es", "de", "fr", "it", "ca"];

    private static readonly Dictionary<string, (string Name, string Email)> Personas = new(StringComparer.Ordinal)
    {
        ["en"] = ("Sally Smith", "sally@example.com"),
        ["es"] = ("María García", "maria@example.com"),
        ["de"] = ("Frieda Fischer", "frieda@example.com"),
        ["fr"] = ("François Dupont", "francois@example.com"),
        ["it"] = ("Giulia Rossi", "giulia@example.com"),
        ["ca"] = ("Jordi Puig", "jordi@example.com"),
    };

    private static readonly string[] SampleDocs = ["Volunteer Agreement", "Privacy Policy"];

    private static readonly (string Name, string? Url)[] SampleResources =
    [
        ("Art Collective Shared Drive", "https://drive.google.com/drive/folders/example"),
        ("art-collective@nobodies.team", "https://groups.google.com/g/art-collective"),
    ];

    private const string FacilitatedMessageSampleText =
        "Hi! I'm organizing the next community event and would love your help. Let me know if you're interested!";

    private readonly record struct PreviewContext(string Culture, string Name, string Email, EmailSettings Settings);

    private static readonly IReadOnlyList<Func<IEmailRenderer, PreviewContext, EmailPreviewItem>> PreviewDefinitions =
    [
        (r, c) => BuildPreviewItem("application-submitted", "Application Submitted (to Admin)", c.Settings.AdminAddress,
            r.RenderApplicationSubmitted(Guid.Empty, c.Name)),
        (r, c) => BuildPreviewItem("signup-rejected", "Signup Rejected", c.Email,
            r.RenderSignupRejected(c.Name, "Incomplete profile information", c.Culture)),
        (r, c) => BuildPreviewItem("reconsent-required", "Re-Consent Required (single doc)", c.Email,
            r.RenderReConsentsRequired(c.Name, [SampleDocs[0]], c.Culture)),
        (r, c) => BuildPreviewItem("reconsents-required", "Re-Consents Required (multiple docs)", c.Email,
            r.RenderReConsentsRequired(c.Name, SampleDocs, c.Culture)),
        (r, c) => BuildPreviewItem("reconsent-reminder", "Re-Consent Reminder", c.Email,
            r.RenderReConsentReminder(c.Name, SampleDocs, 14, c.Culture)),
        (r, c) => BuildPreviewItem("welcome", "Welcome", c.Email,
            r.RenderWelcome(c.Name, c.Culture)),
        (r, c) => BuildPreviewItem("access-suspended", "Access Suspended", c.Email,
            r.RenderAccessSuspended(c.Name, "Outstanding consent requirements", c.Culture)),
        (r, c) => BuildPreviewItem("email-verification", "Email Verification", "newemail@example.com",
            r.RenderEmailVerification(c.Name, "newemail@example.com", $"{c.Settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", culture: c.Culture)),
        (r, c) => BuildPreviewItem("email-verification-merge", "Email Verification (Merge)", "duplicate@example.com",
            r.RenderEmailVerification(c.Name, "duplicate@example.com", $"{c.Settings.BaseUrl}/Profile/VerifyEmail?token=sample-token", isConflict: true, culture: c.Culture)),
        (r, c) => BuildPreviewItem("deletion-requested", "Account Deletion Requested", c.Email,
            r.RenderAccountDeletionRequested(c.Name, "March 15, 2026", c.Culture)),
        (r, c) => BuildPreviewItem("account-deleted", "Account Deleted", c.Email,
            r.RenderAccountDeleted(c.Name, c.Culture)),
        (r, c) => BuildPreviewItem("added-to-team", "Added to Team", c.Email,
            r.RenderAddedToTeam(c.Name, "Art Collective", "art-collective", SampleResources, c.Culture)),
        (r, c) => BuildPreviewItem("facilitated-message", "Facilitated Message (with contact info)", c.Email,
            r.RenderFacilitatedMessage(c.Name, "Alex Firestone", FacilitatedMessageSampleText, true, "alex@example.com", c.Culture)),
        (r, c) => BuildPreviewItem("facilitated-message-anon", "Facilitated Message (without contact info)", c.Email,
            r.RenderFacilitatedMessage(c.Name, "Alex Firestone", FacilitatedMessageSampleText, false, null, c.Culture)),
        (r, c) => BuildPreviewItem("google-group-removal-loss", "Google Group Removal — Loss of Access", c.Email,
            r.RenderGoogleGroupRemovalLossOfAccess(c.Name, "Art Collective", "art-collective@nobodies.team", c.Culture)),
        (r, c) => BuildPreviewItem("google-drive-removal-loss", "Google Drive Removal — Loss of Access", c.Email,
            r.RenderGoogleDriveRemovalLossOfAccess(c.Name, "Art Collective Shared Drive", c.Culture)),
        (r, c) => BuildPreviewItem("google-removal-secondary-cleanup", "Google Access Removal — Secondary Email Cleanup", "old-" + c.Email,
            r.RenderGoogleAccessRemovalSecondaryCleanup(c.Name, "old-" + c.Email, c.Email, c.Culture)),
    ];

    private static EmailPreviewItem BuildPreviewItem(string id, string name, string recipient, EmailContent content) => new()
    {
        Id = id,
        Name = name,
        Recipient = recipient,
        Subject = content.Subject,
        Body = content.HtmlBody
    };

    [HttpGet("EmailPreview")]
    public IActionResult EmailPreview(
        [FromServices] IEmailRenderer renderer,
        [FromServices] IEmailBodyComposer bodyComposer,
        [FromServices] IOptions<EmailSettings> emailSettings,
        [FromServices] IEnumerable<IEmailPreviewContributor> contributors)
    {
        var settings = emailSettings.Value;
        var contributorList = contributors.ToList();
        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in Cultures)
        {
            var (name, email) = Personas[culture];
            var ctx = new PreviewContext(culture, name, email, settings);

            // Both sources render while the templates migrate to their sending
            // sections (peterdrier/Humans#1651): a contributed sample supersedes the
            // legacy row for the same id, so a half-migrated template shows once.
            var contributed = contributorList
                .SelectMany(c => c.Samples(new EmailPreviewPersona(culture, name, email)))
                .Select(ToPreviewItem)
                .ToList();
            var contributedIds = contributed.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

            previews[culture] = PreviewDefinitions
                .Select(build => build(renderer, ctx))
                .Where(item => !contributedIds.Contains(item.Id))
                .Concat(contributed)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item =>
                {
                    item.Body = bodyComposer.Compose(item.Body).HtmlBody;
                    return item;
                })
                .ToList();
        }

        return View(new EmailPreviewViewModel { Previews = previews, FromAddress = settings.FromAddress });
    }

    private static EmailPreviewItem ToPreviewItem(EmailPreviewSample sample) => new()
    {
        Id = sample.Id,
        Name = sample.Name,
        Recipient = sample.Message.RecipientEmail,
        Subject = sample.Message.Subject,
        Body = sample.Message.HtmlBody
    };
}
