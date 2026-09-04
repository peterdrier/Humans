using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Humans.Backdoor.Filters;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Issues.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// The in-app issue queue, for an agent running triage.
/// </summary>
/// <remarks>
/// Every write passes <c>ActorUserId</c> — the human the presented key belongs to, resolved by
/// <see cref="BackdoorApiKeyAuthFilter"/>.
/// </remarks>
[ApiController]
[Route("api/backdoor/issues")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorIssuesController(
    IIssueTriage issues,
    IUserServiceRead users,
    ILogger<BackdoorIssuesController> logger)
    : ApiControllerBase(users)
{
    /// <summary>Ceiling on <c>?limit=</c>, matching the section's other list endpoints.</summary>
    private const int MaxLimit = 1000;

    /// <summary>The key owner, as recorded on everything this controller writes.</summary>
    private Guid ActorUserId => GetCurrentUserId() ?? Guid.Empty;

    /// <summary>
    /// The key owner's active roles, as installed by <see cref="BackdoorApiKeyAuthFilter"/>.
    /// The queue is scoped to these exactly as it is for that person in the browser.
    /// </summary>
    private IReadOnlyList<string> ViewerRoles =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

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
            Limit: Math.Clamp(limit, 1, MaxLimit));

        var rows = await issues.GetIssueListAsync(
            filter,
            viewerUserId: ActorUserId,
            viewerRoles: ViewerRoles,
            viewerIsAdmin: User.IsInRole(RoleNames.Admin));

        return Ok(rows.Select(MapList));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var thread = await issues.GetThreadAsync(id);
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
            var issueId = await issues.CreateIssueAsync(
                reporterUserId: model.ReporterUserId,
                category: model.Category,
                title: model.Title,
                description: model.Description,
                section: model.Section,
                dueDate: model.DueDate,
                actorUserId: ActorUserId);

            logger.LogInformation("Issue {IssueId} created via API for reporter {ReporterId}", issueId, model.ReporterUserId);
            return Ok(new { id = issueId });
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
            c.CommentId,
            At = c.At.ToDateTimeUtc(),
            c.ActorUserId,
            ActorName = c.ActorDisplayName,
            c.ActorIsReporter,
            c.Content
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
                senderUserId: ActorUserId,
                content: model.Content);

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
    public Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateIssueStatusModel model) =>
        PatchAsync(id, "status", () => issues.UpdateStatusAsync(id, model.Status, ActorUserId));

    [HttpPatch("{id}/assignee")]
    public Task<IActionResult> UpdateAssignee(Guid id, [FromBody] UpdateIssueAssigneeModel model) =>
        PatchAsync(id, "assignee", () => issues.UpdateAssigneeAsync(id, model.AssigneeUserId, ActorUserId));

    [HttpPatch("{id}/section")]
    public Task<IActionResult> UpdateSection(Guid id, [FromBody] UpdateIssueSectionModel model) =>
        PatchAsync(id, "section", () => issues.UpdateSectionAsync(id, model.Section, ActorUserId));

    [HttpPatch("{id}/github-issue")]
    public Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetIssueGitHubIssueModel model) =>
        PatchAsync(id, "github-issue", () => issues.SetGitHubIssueNumberAsync(id, model.GitHubIssueNumber, ActorUserId));

    /// <summary>
    /// The one shape every <c>PATCH /api/backdoor/issues/{id}/*</c> endpoint has: apply the
    /// one-field change, answer <c>{success:true}</c>, and map failure the same way whichever
    /// field moved — a missing issue to 404, a rejected move to 422 carrying the service's
    /// reason, anything else to 500.
    /// </summary>
    /// <remarks>
    /// The 422 arm used to exist on <c>section</c> alone; the others turned a state-machine
    /// rejection into a misleading 404. Normalising that is what made one pipeline possible
    /// at all — the per-endpoint failure strings were the parameter bag standing in the way.
    /// </remarks>
    private async Task<IActionResult> PatchAsync(Guid id, string field, Func<Task> apply)
    {
        try
        {
            await apply();
            logger.LogInformation("Issue {IssueId} {Field} updated via API", id, field);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Issue {IssueId} not found during API {Field} patch: {Reason}", id, field, ex.Message);
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Issue {IssueId} API {Field} patch rejected: {Reason}", id, field, ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update {Field} on issue {IssueId}", field, id);
            return StatusCode(500, new { error = $"Failed to update {field}" });
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
        i.ReporterUserId,
        ReporterLanguage = displayUsers?.GetValueOrDefault(i.ReporterUserId)?.PreferredLanguage,
        i.AssigneeUserId,
        AssigneeName = i.AssigneeUserId is { } assigneeId
            ? displayUsers?.GetValueOrDefault(assigneeId)?.BurnerName
            : null,
        i.GitHubIssueNumber,
        i.DueDate,
        ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
        CreatedAt = i.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
        ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
        i.CommentCount
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
        i.ReporterEmail,
        i.ReporterUserId,
        ReporterLanguage = i.ReporterPreferredLanguage,
        i.AssigneeUserId,
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

        return await UserService.GetUserInfosAsync(ids);
    }
}

internal sealed class ApiCreateIssueModel
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

internal sealed class PostIssueCommentModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;
}

internal sealed class UpdateIssueStatusModel
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueStatus Status { get; set; }
}

internal sealed class UpdateIssueAssigneeModel
{
    public Guid? AssigneeUserId { get; set; }
}

internal sealed class UpdateIssueSectionModel
{
    public string? Section { get; set; }
}

internal sealed class SetIssueGitHubIssueModel
{
    public int? GitHubIssueNumber { get; set; }
}
