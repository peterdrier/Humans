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

    public FeedbackApiController(IFeedbackService feedbackService)
    {
        _feedbackService = feedbackService;
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
            ReporterUserId = r.UserId,
            r.AdminNotes,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath != null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            AdminResponseSentAt = r.AdminResponseSentAt?.ToDateTimeUtc(),
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
        if (r == null) return NotFound();

        var responseCounts = await _feedbackService.GetResponseCountsAsync([id]);

        return Ok(new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            ReporterName = r.User.DisplayName,
            ReporterUserId = r.UserId,
            r.AdminNotes,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath != null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            AdminResponseSentAt = r.AdminResponseSentAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            ResponseCount = responseCounts.GetValueOrDefault(r.Id)
        });
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model)
    {
        // API has no user context — pass null actor
        await _feedbackService.UpdateStatusAsync(id, model.Status, null);
        return Ok(new { success = true });
    }

    [HttpPatch("{id}/notes")]
    public async Task<IActionResult> UpdateNotes(Guid id, [FromBody] UpdateFeedbackNotesModel model)
    {
        await _feedbackService.UpdateAdminNotesAsync(id, model.Notes);
        return Ok(new { success = true });
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetGitHubIssueModel model)
    {
        await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
        return Ok(new { success = true });
    }

    [HttpPost("{id}/respond")]
    public async Task<IActionResult> SendResponse(Guid id, [FromBody] SendFeedbackResponseModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        await _feedbackService.SendResponseAsync(id, model.Message, null);
        return Ok(new { success = true });
    }
}
