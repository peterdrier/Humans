using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;

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
        var reports = await _feedbackService.GetFeedbackListAsync(status, category, limit);
        var responseCounts = await _feedbackService.GetResponseCountsAsync(reports.Select(r => r.Id));

        var result = reports.Select(r => new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            // TODO: AdminNotes removed in feedback upgrade
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            // TODO: AdminResponseSentAt removed in feedback upgrade
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            ResponseCount = responseCounts.GetValueOrDefault(r.Id)
        });

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var r = await _feedbackService.GetFeedbackByIdAsync(id);
        if (r is null) return NotFound();

        var responseDetails = await _feedbackService.GetResponseDetailsAsync(id);

        return Ok(new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            // TODO: AdminNotes removed in feedback upgrade
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            // TODO: AdminResponseSentAt removed in feedback upgrade
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            ResponseCount = responseDetails.Count,
            Responses = responseDetails.Select(rd => new
            {
                rd.SentAt,
                rd.ActorName
            })
        });
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model)
    {
        try
        {
            // API has no user context — pass null actor
            await _feedbackService.UpdateStatusAsync(id, model.Status, null);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/notes")]
    public async Task<IActionResult> UpdateNotes(Guid id, [FromBody] UpdateFeedbackNotesModel model)
    {
        try
        {
            await _feedbackService.UpdateAdminNotesAsync(id, model.Notes);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} notes", id);
            return StatusCode(500, new { error = "Failed to update notes" });
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }

    [HttpPost("{id}/respond")]
    public async Task<IActionResult> SendResponse(Guid id, [FromBody] SendFeedbackResponseModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            await _feedbackService.SendResponseAsync(id, model.Message, null);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to send response" });
        }
    }
}
