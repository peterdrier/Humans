using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces.Issues;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;

// Issue cross-domain nav properties (Reporter, Assignee, ResolvedByUser) and
// IssueComment.SenderUser are [Obsolete] — IssuesService stitches them in memory
// from IUserService so controllers can continue to read them for response shaping.
// Nav-strip follow-up tracked in design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/issues")]
[ServiceFilter(typeof(IssuesApiKeyAuthFilter))]
public class IssuesApiController : ControllerBase
{
    private readonly IIssuesService _issues;
    private readonly ILogger<IssuesApiController> _logger;

    public IssuesApiController(
        IIssuesService issues,
        ILogger<IssuesApiController> logger)
    {
        _issues = issues;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] IssueStatus? status,
        [FromQuery] IssueCategory? category,
        [FromQuery] string? section,
        [FromQuery] Guid? assignee,
        [FromQuery] Guid? reporter = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50)
    {
        var filter = new IssueListFilter(
            Statuses: status.HasValue ? new[] { status.Value } : null,
            Categories: category.HasValue ? new[] { category.Value } : null,
            Sections: section is not null ? new string?[] { section } : null,
            ReporterUserId: reporter,
            AssigneeUserId: assignee,
            SearchText: string.IsNullOrWhiteSpace(search) ? null : search,
            Limit: limit);

        var issues = await _issues.GetIssueListAsync(
            filter,
            viewerUserId: Guid.Empty,
            viewerRoles: Array.Empty<string>(),
            viewerIsAdmin: true);

        return Ok(issues.Select(MapList));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var thread = await _issues.GetThreadAsync(id);
        return Ok(MapDetail(issue, thread));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ApiCreateIssueModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var issue = await _issues.SubmitIssueAsync(
                reporterUserId: model.ReporterUserId,
                category: model.Category,
                title: model.Title,
                description: model.Description,
                section: model.Section,
                pageUrl: null,
                userAgent: null,
                additionalContext: null,
                screenshot: null,
                dueDate: model.DueDate);

            _logger.LogInformation("Issue {IssueId} created via API for reporter {ReporterId}", issue.Id, model.ReporterUserId);
            return Ok(new { id = issue.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create issue via API for reporter {ReporterId}", model.ReporterUserId);
            return StatusCode(500, new { error = "Failed to create issue" });
        }
    }

    [HttpGet("{id}/comments")]
    public async Task<IActionResult> GetComments(Guid id)
    {
        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var thread = await _issues.GetThreadAsync(id);
        var comments = thread.OfType<IssueCommentEvent>().Select(c => new
        {
            CommentId = c.CommentId,
            At = c.At.ToDateTimeUtc(),
            ActorUserId = c.ActorUserId,
            ActorName = c.ActorDisplayName,
            ActorIsReporter = c.ActorIsReporter,
            Content = c.Content
        });

        return Ok(comments);
    }

    [HttpPost("{id}/comments")]
    public async Task<IActionResult> PostComment(Guid id, [FromBody] PostIssueCommentModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var comment = await _issues.PostCommentAsync(
                issueId: id,
                senderUserId: null,
                content: model.Content,
                senderIsReporter: false);

            _logger.LogInformation("Comment {CommentId} posted on issue {IssueId} via API", comment.Id, id);
            return Ok(new
            {
                comment.Id,
                comment.Content,
                CreatedAt = comment.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (InvalidOperationException)
        {
            // Service throws InvalidOperationException when the issue id
            // doesn't resolve to a row — surface as 404. Log at Warning so
            // the event stays visible in prod (always-log-problems.md).
            _logger.LogWarning("Issue {IssueId} not found during API PostComment", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post comment on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to post comment" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateIssueStatusModel model)
    {
        try
        {
            await _issues.UpdateStatusAsync(id, model.Status, actorUserId: null);
            _logger.LogInformation("Issue {IssueId} status changed to {Status} via API", id, model.Status);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // Service throws InvalidOperationException when the issue id
            // doesn't resolve to a row — surface as 404. Log at Warning so
            // the event stays visible in prod (always-log-problems.md).
            _logger.LogWarning("Issue {IssueId} not found during API UpdateStatus", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update issue {IssueId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/assignee")]
    public async Task<IActionResult> UpdateAssignee(Guid id, [FromBody] UpdateIssueAssigneeModel model)
    {
        try
        {
            await _issues.UpdateAssigneeAsync(id, model.AssigneeUserId, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // Service throws InvalidOperationException when the issue id
            // doesn't resolve to a row — surface as 404. Log at Warning so
            // the event stays visible in prod (always-log-problems.md).
            _logger.LogWarning("Issue {IssueId} not found during API UpdateAssignee", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update assignee on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to update assignee" });
        }
    }

    [HttpPatch("{id}/section")]
    public async Task<IActionResult> UpdateSection(Guid id, [FromBody] UpdateIssueSectionModel model)
    {
        try
        {
            await _issues.UpdateSectionAsync(id, model.Section, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            // Log at Warning so the event stays visible in prod
            // (always-log-problems.md). Exception object dropped — the
            // race-style "not found" carries no useful stack.
            _logger.LogWarning("Issue {IssueId} not found during API UpdateSection: {Reason}", id, ex.Message);
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // State-machine violation (e.g. issue is terminal) — surface as 422.
            // Log at Warning per always-log-problems.md so reject events are
            // visible in the prod log viewer.
            _logger.LogWarning("Issue {IssueId} API UpdateSection rejected: {Reason}", id, ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update section on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to update section" });
        }
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetIssueGitHubIssueModel model)
    {
        try
        {
            await _issues.SetGitHubIssueNumberAsync(id, model.GitHubIssueNumber, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // Service throws InvalidOperationException when the issue id
            // doesn't resolve to a row — surface as 404. Log at Warning so
            // the event stays visible in prod (always-log-problems.md).
            _logger.LogWarning("Issue {IssueId} not found during API SetGitHubIssue", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue number on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }

    private static object MapList(Issue i) => new
    {
        i.Id,
        Status = i.Status.ToString(),
        Category = i.Category.ToString(),
        i.Section,
        i.Title,
        i.Description,
        i.PageUrl,
        i.UserAgent,
        i.AdditionalContext,
        ReporterName = i.Reporter?.DisplayName,
        ReporterEmail = i.Reporter?.Email,
        ReporterUserId = i.ReporterUserId,
        ReporterLanguage = i.Reporter?.PreferredLanguage,
        AssigneeUserId = i.AssigneeUserId,
        AssigneeName = i.Assignee?.DisplayName,
        i.GitHubIssueNumber,
        i.DueDate,
        ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
        CreatedAt = i.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
        ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
        CommentCount = i.Comments.Count
    };

    private static object MapDetail(Issue i, IReadOnlyList<IssueThreadEvent> thread) => new
    {
        issue = MapList(i),
        thread = thread.Select(e => e switch
        {
            IssueCommentEvent c => (object)new
            {
                type = "comment",
                at = c.At.ToDateTimeUtc(),
                actorUserId = c.ActorUserId,
                actorName = c.ActorDisplayName,
                actorIsReporter = c.ActorIsReporter,
                content = c.Content
            },
            IssueAuditEvent a => new
            {
                type = "audit",
                at = a.At.ToDateTimeUtc(),
                actorUserId = a.ActorUserId,
                actorName = a.ActorDisplayName,
                action = a.Action.ToString(),
                description = a.Description
            },
            _ => throw new NotSupportedException()
        })
    };
}

public class ApiCreateIssueModel
{
    [Required]
    public Guid ReporterUserId { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueCategory Category { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(5000)]
    public string Description { get; set; } = string.Empty;

    public string? Section { get; set; }

    public LocalDate? DueDate { get; set; }
}
