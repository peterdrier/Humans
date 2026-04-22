using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;

// FeedbackReport / FeedbackMessage cross-domain nav properties (User, ResolvedByUser,
// AssignedToUser, AssignedToTeam, SenderUser) are [Obsolete] — FeedbackService stitches
// them in memory from IUserService / ITeamService so controllers can continue to read
// them for response shaping. Nav-strip follow-up tracked in design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/feedback")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class FeedbackApiController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly ILogger<FeedbackApiController> _logger;

    public FeedbackApiController(
        IFeedbackService feedbackService,
        ILogger<FeedbackApiController> logger)
    {
        _feedbackService = feedbackService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] FeedbackStatus? status,
        [FromQuery] FeedbackCategory? category,
        [FromQuery] int limit = 50)
    {
        var reports = await _feedbackService.GetFeedbackListAsync(status, category, limit: limit);

        var result = reports.Select(r => new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            MessageCount = r.Messages.Count,
            AssignedToUserId = r.AssignedToUserId,
            AssignedToName = r.AssignedToUser?.DisplayName,
            AssignedToTeamId = r.AssignedToTeamId,
            AssignedToTeamName = r.AssignedToTeam?.Name
        });

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var r = await _feedbackService.GetFeedbackByIdAsync(id);
        if (r is null) return NotFound();

        return Ok(new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            AssignedToUserId = r.AssignedToUserId,
            AssignedToName = r.AssignedToUser?.DisplayName,
            AssignedToTeamId = r.AssignedToTeamId,
            AssignedToTeamName = r.AssignedToTeam?.Name,
            Messages = r.Messages.Select(m => new
            {
                m.Id,
                SenderName = m.SenderUser?.DisplayName ?? "Unknown",
                m.SenderUserId,
                m.Content,
                CreatedAt = m.CreatedAt.ToDateTimeUtc(),
                IsReporter = m.SenderUserId.HasValue && m.SenderUserId == r.UserId
            })
        });
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var messages = await _feedbackService.GetMessagesAsync(id);

        return Ok(messages.Select(m => new
        {
            m.Id,
            SenderName = m.SenderUser?.DisplayName ?? "Unknown",
            m.SenderUserId,
            m.Content,
            CreatedAt = m.CreatedAt.ToDateTimeUtc(),
            IsReporter = m.SenderUserId.HasValue && m.SenderUserId == report.UserId
        }));
    }

    [HttpPost("{id}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostFeedbackMessageModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var message = await _feedbackService.PostMessageAsync(id, null, model.Content, isAdmin: true);
            return Ok(new
            {
                message.Id,
                message.Content,
                CreatedAt = message.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to post message" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model)
    {
        try
        {
            await _feedbackService.UpdateStatusAsync(id, model.Status, null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/assignment")]
    public async Task<IActionResult> UpdateAssignment(Guid id, [FromBody] UpdateFeedbackAssignmentModel model)
    {
        try
        {
            await _feedbackService.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update assignment for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to update assignment" });
        }
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }
}
