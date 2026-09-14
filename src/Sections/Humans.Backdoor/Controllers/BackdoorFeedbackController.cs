using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Humans.Backdoor.Filters;
using Humans.Base.Controllers;
using Humans.Feedback.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// The feedback queue, for an agent working the thread.
/// </summary>
/// <remarks>
/// Every mutation carries <c>ActorUserId</c> — the human the presented key belongs
/// to, resolved by <see cref="BackdoorApiKeyAuthFilter"/>.
/// </remarks>
[ApiController]
[Route("api/backdoor/feedback")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorFeedbackController(
    IFeedbackTriage feedback,
    IUserServiceRead users,
    ILogger<BackdoorFeedbackController> logger)
    : ApiControllerBase(users)
{
    /// <summary>Ceiling on <c>?limit=</c>, matching the section's other list endpoints.</summary>
    private const int MaxLimit = 1000;

    /// <summary>The key owner, as recorded on everything this controller writes.</summary>
    private Guid ActorUserId => GetCurrentUserId() ?? Guid.Empty;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] FeedbackStatus? status,
        [FromQuery] FeedbackCategory? category,
        [FromQuery] int limit = 50)
    {
        var reports = await feedback.GetFeedbackListAsync(
            status, category, limit: Math.Clamp(limit, 1, MaxLimit));
        return Ok(reports.Select(MapSummary));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var report = await feedback.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        return Ok(new
        {
            report.Id,
            Category = report.Category.ToString(),
            Status = report.Status.ToString(),
            report.Description,
            report.PageUrl,
            report.UserAgent,
            report.AdditionalContext,
            report.ReporterName,
            report.ReporterEmail,
            ReporterUserId = report.UserId,
            report.ReporterLanguage,
            report.GitHubIssueNumber,
            ScreenshotUrl = report.ScreenshotStoragePath is not null ? $"/{report.ScreenshotStoragePath}" : null,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = report.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = report.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            report.ResolvedByName,
            report.AssignedToUserId,
            report.AssignedToName,
            report.AssignedToTeamId,
            report.AssignedToTeamName,
            Messages = report.Messages.Select(m => MapMessage(m, report.UserId))
        });
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var report = await feedback.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        return Ok(report.Messages.Select(m => MapMessage(m, report.UserId)));
    }

    [HttpPost("{id}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostFeedbackMessageModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var message = await feedback.PostMessageAsync(id, ActorUserId, model.Content);
            return Ok(new
            {
                message.Id,
                message.Content,
                CreatedAt = message.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Feedback {FeedbackId} not found during API PostMessage", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to post message" });
        }
    }

    [HttpPatch("{id}/status")]
    public Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model) =>
        PatchAsync(id, "status", () => feedback.UpdateStatusAsync(id, model.Status, ActorUserId));

    [HttpPatch("{id}/assignment")]
    public Task<IActionResult> UpdateAssignment(Guid id, [FromBody] UpdateFeedbackAssignmentModel model) =>
        PatchAsync(id, "assignment", () => feedback.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, ActorUserId));

    [HttpPatch("{id}/github-issue")]
    public Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetFeedbackGitHubIssueModel model) =>
        PatchAsync(id, "github-issue", () => feedback.SetGitHubIssueNumberAsync(id, model.IssueNumber, ActorUserId));

    /// <summary>
    /// The one shape every <c>PATCH /api/backdoor/feedback/{id}/*</c> endpoint has: apply the
    /// one-field change, answer <c>{success:true}</c>, and map failure the same way whichever
    /// field moved — a missing report to 404, a rejected move to 422 carrying the service's
    /// reason, anything else to 500.
    /// </summary>
    /// <remarks>
    /// Deliberately a twin of <c>BackdoorIssuesController.PatchAsync</c> rather than something
    /// shared: the target shape collapses within a controller, not across them.
    /// </remarks>
    private async Task<IActionResult> PatchAsync(Guid id, string field, Func<Task> apply)
    {
        try
        {
            await apply();
            logger.LogInformation("Feedback {FeedbackId} {Field} updated via API", id, field);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Feedback {FeedbackId} not found during API {Field} patch: {Reason}", id, field, ex.Message);
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Feedback {FeedbackId} API {Field} patch rejected: {Reason}", id, field, ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update {Field} on feedback {FeedbackId}", field, id);
            return StatusCode(500, new { error = $"Failed to update {field}" });
        }
    }

    private static object MapSummary(FeedbackReportInfo r) => new
    {
        r.Id,
        Category = r.Category.ToString(),
        Status = r.Status.ToString(),
        r.Description,
        r.PageUrl,
        r.UserAgent,
        r.AdditionalContext,
        r.ReporterName,
        r.ReporterEmail,
        ReporterUserId = r.UserId,
        r.ReporterLanguage,
        r.GitHubIssueNumber,
        ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
        CreatedAt = r.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
        LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
        LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
        ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
        r.ResolvedByName,
        MessageCount = r.Messages.Count,
        r.AssignedToUserId,
        r.AssignedToName,
        r.AssignedToTeamId,
        r.AssignedToTeamName
    };

    private static object MapMessage(FeedbackMessageInfo m, Guid reporterUserId) => new
    {
        m.Id,
        SenderName = m.SenderName ?? "Unknown",
        m.SenderUserId,
        m.Content,
        CreatedAt = m.CreatedAt.ToDateTimeUtc(),
        IsReporter = m.SenderUserId.HasValue && m.SenderUserId == reporterUserId
    };
}

internal sealed class PostFeedbackMessageModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;
}

internal sealed class UpdateFeedbackStatusModel
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeedbackStatus Status { get; set; }
}

internal sealed class UpdateFeedbackAssignmentModel
{
    public Guid? AssignedToUserId { get; set; }

    public Guid? AssignedToTeamId { get; set; }
}

internal sealed class SetFeedbackGitHubIssueModel
{
    public int? IssueNumber { get; set; }
}
