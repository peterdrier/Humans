using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/issues")]
[ServiceFilter(typeof(IssuesApiKeyAuthFilter))]
public class IssuesApiController(IIssuesService issues, IUserServiceRead users, ILogger<IssuesApiController> logger)
    : ControllerBase
{
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
            Statuses: status.HasValue ? [status.Value] : null,
            Categories: category.HasValue ? [category.Value] : null,
            Sections: section is not null ? [section] : null,
            ReporterUserId: reporter,
            AssigneeUserId: assignee,
            SearchText: string.IsNullOrWhiteSpace(search) ? null : search,
            Limit: limit);

        var issues1 = await issues.GetIssueListAsync(
            filter,
            viewerUserId: Guid.Empty,
            viewerRoles: [],
            viewerIsAdmin: true);

        return Ok(issues1.Select(MapList));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var thread = await issues.GetThreadAsync(id);
        // ReporterEmail sourced from UserInfo (not User.Email) — keeps shape parity with the list endpoint. See PR 618.
        var displayUsers = await GetIssueDisplayUsersAsync(issue);
        return Ok(MapDetail(issue, thread, displayUsers));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ApiCreateIssueModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var issue = await issues.SubmitIssueAsync(
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

            logger.LogInformation("Issue {IssueId} created via API for reporter {ReporterId}", issue.Id, model.ReporterUserId);
            return Ok(new { id = issue.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create issue via API for reporter {ReporterId}", model.ReporterUserId);
            return StatusCode(500, new { error = "Failed to create issue" });
        }
    }

    [HttpGet("{id}/comments")]
    public async Task<IActionResult> GetComments(Guid id)
    {
        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var thread = await issues.GetThreadAsync(id);
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
            var comment = await issues.PostCommentAsync(
                issueId: id,
                senderUserId: null,
                content: model.Content,
                senderIsReporter: false);

            logger.LogInformation("Comment {CommentId} posted on issue {IssueId} via API", comment.Id, id);
            return Ok(new
            {
                comment.Id,
                comment.Content,
                CreatedAt = comment.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (InvalidOperationException)
        {
            // 404 on missing — log Warning per always-log-problems.
            logger.LogWarning("Issue {IssueId} not found during API PostComment", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post comment on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to post comment" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateIssueStatusModel model)
    {
        try
        {
            await issues.UpdateStatusAsync(id, model.Status, actorUserId: null);
            logger.LogInformation("Issue {IssueId} status changed to {Status} via API", id, model.Status);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // 404 on missing — log Warning per always-log-problems.
            logger.LogWarning("Issue {IssueId} not found during API UpdateStatus", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update issue {IssueId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/assignee")]
    public async Task<IActionResult> UpdateAssignee(Guid id, [FromBody] UpdateIssueAssigneeModel model)
    {
        try
        {
            await issues.UpdateAssigneeAsync(id, model.AssigneeUserId, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // 404 on missing — log Warning per always-log-problems.
            logger.LogWarning("Issue {IssueId} not found during API UpdateAssignee", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update assignee on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to update assignee" });
        }
    }

    [HttpPatch("{id}/section")]
    public async Task<IActionResult> UpdateSection(Guid id, [FromBody] UpdateIssueSectionModel model)
    {
        try
        {
            await issues.UpdateSectionAsync(id, model.Section, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            // 404 on missing — log Warning per always-log-problems.
            logger.LogWarning("Issue {IssueId} not found during API UpdateSection: {Reason}", id, ex.Message);
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // State-machine violation (terminal status) → 422.
            logger.LogWarning("Issue {IssueId} API UpdateSection rejected: {Reason}", id, ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update section on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to update section" });
        }
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetIssueGitHubIssueModel model)
    {
        try
        {
            await issues.SetGitHubIssueNumberAsync(id, model.GitHubIssueNumber, actorUserId: null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            // 404 on missing — log Warning per always-log-problems.
            logger.LogWarning("Issue {IssueId} not found during API SetGitHubIssue", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set GitHub issue number on issue {IssueId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }

    private static object MapDetailIssue(IssueDetail i, IReadOnlyDictionary<Guid, UserInfo>? displayUsers = null) => new
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
        ReporterName = displayUsers?.GetValueOrDefault(i.ReporterUserId)?.BurnerName,
        // ReporterEmail from UserInfo (not User.Email) for shape parity with list endpoint.
        ReporterEmail = displayUsers?.GetValueOrDefault(i.ReporterUserId)?.Email,
        ReporterUserId = i.ReporterUserId,
        ReporterLanguage = displayUsers?.GetValueOrDefault(i.ReporterUserId)?.PreferredLanguage,
        AssigneeUserId = i.AssigneeUserId,
        AssigneeName = i.AssigneeUserId is { } assigneeId
            ? displayUsers?.GetValueOrDefault(assigneeId)?.BurnerName
            : null,
        i.GitHubIssueNumber,
        i.DueDate,
        ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
        CreatedAt = i.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
        ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
        CommentCount = i.CommentCount
    };

    private static object MapList(IssueListSnapshot i) => new
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
        ReporterName = i.ReporterDisplayName,
        ReporterEmail = i.ReporterEmail,
        ReporterUserId = i.ReporterUserId,
        ReporterLanguage = i.ReporterPreferredLanguage,
        AssigneeUserId = i.AssigneeUserId,
        AssigneeName = i.AssigneeDisplayName,
        i.GitHubIssueNumber,
        i.DueDate,
        ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
        CreatedAt = i.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
        ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
        i.CommentCount
    };

    private static object MapDetail(
        IssueDetail i,
        IReadOnlyList<IssueThreadEvent> thread,
        IReadOnlyDictionary<Guid, UserInfo> displayUsers) => new
        {
            issue = MapDetailIssue(i, displayUsers),
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

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> GetIssueDisplayUsersAsync(IssueDetail issue)
    {
        var ids = new HashSet<Guid> { issue.ReporterUserId };
        if (issue.AssigneeUserId is { } assigneeId) ids.Add(assigneeId);
        if (issue.ResolvedByUserId is { } resolvedById) ids.Add(resolvedById);

        return await users.GetUserInfosAsync(ids);
    }
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
