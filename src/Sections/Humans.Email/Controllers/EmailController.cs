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

    // Persona + culture data for the gallery, held here so EmailPreview itself is just
    // "loop cultures, ask the contributors, return the view".
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

    /// <summary>
    /// The template gallery: every sending section contributes its own samples through
    /// <see cref="IEmailPreviewContributor"/>, so the gallery cannot drift from what the
    /// sections actually send (peterdrier/Humans#1651). Ordinal by sample id.
    /// </summary>
    [HttpGet("EmailPreview")]
    public IActionResult EmailPreview(
        [FromServices] IEmailBodyComposer bodyComposer,
        [FromServices] IOptions<EmailSettings> emailSettings,
        [FromServices] IEnumerable<IEmailPreviewContributor> contributors)
    {
        var contributorList = contributors.ToList();
        var previews = new Dictionary<string, List<EmailPreviewItem>>(StringComparer.Ordinal);

        foreach (var culture in Cultures)
        {
            var (name, email) = Personas[culture];

            previews[culture] = contributorList
                .SelectMany(c => c.Samples(new EmailPreviewPersona(culture, name, email)))
                .Select(ToPreviewItem)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item =>
                {
                    item.Body = bodyComposer.Compose(item.Body).HtmlBody;
                    return item;
                })
                .ToList();
        }

        return View(new EmailPreviewViewModel { Previews = previews, FromAddress = emailSettings.Value.FromAddress });
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
