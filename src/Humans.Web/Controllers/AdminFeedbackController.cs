using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin/Feedback")]
public class AdminFeedbackController : HumansControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly ILogger<AdminFeedbackController> _logger;

    public AdminFeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager,
        ILogger<AdminFeedbackController> logger)
        : base(userManager)
    {
        _feedbackService = feedbackService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(FeedbackStatus? status, FeedbackCategory? category)
    {
        var reports = await _feedbackService.GetFeedbackListAsync(status, category);

        var viewModel = new FeedbackListViewModel
        {
            StatusFilter = status,
            CategoryFilter = category,
            Reports = reports.Select(r => new FeedbackListItemViewModel
            {
                Id = r.Id,
                Category = r.Category,
                Status = r.Status,
                Description = r.Description.Length > 100 ? r.Description[..100] + "..." : r.Description,
                ReporterName = r.User.DisplayName,
                ReporterUserId = r.UserId,
                PageUrl = r.PageUrl,
                CreatedAt = r.CreatedAt.ToDateTimeUtc(),
                HasScreenshot = r.ScreenshotStoragePath is not null
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null)
            return NotFound();

        var viewModel = new FeedbackDetailViewModel
        {
            Id = report.Id,
            Category = report.Category,
            Status = report.Status,
            Description = report.Description,
            PageUrl = report.PageUrl,
            UserAgent = report.UserAgent,
            ScreenshotUrl = report.ScreenshotStoragePath is not null ? $"/{report.ScreenshotStoragePath}" : null,
            ReporterName = report.User.DisplayName,
            ReporterUserId = report.UserId,
            // TODO: AdminNotes removed in feedback upgrade
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            // TODO: AdminResponseSentAt removed in feedback upgrade
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByUser?.DisplayName
        };

        return View(viewModel);
    }

    [HttpPost("{id}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateFeedbackStatusModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await _feedbackService.UpdateStatusAsync(id, model.Status, user.Id);
            SetSuccess("Status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            SetError("Failed to update status.");
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id}/Notes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateNotes(Guid id, UpdateFeedbackNotesModel model)
    {
        try
        {
            await _feedbackService.UpdateAdminNotesAsync(id, model.Notes);
            SetSuccess("Notes saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} notes", id);
            SetError("Failed to save notes.");
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            SetSuccess("GitHub issue linked.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            SetError("Failed to link GitHub issue.");
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id}/Respond")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendResponse(Guid id, SendFeedbackResponseModel model)
    {
        if (!ModelState.IsValid)
        {
            SetError("Response message is required.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await _feedbackService.SendResponseAsync(id, model.Message, user.Id);
            SetSuccess("Response sent.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response for feedback {FeedbackId}", id);
            SetError("Failed to send response.");
        }
        return RedirectToAction(nameof(Detail), new { id });
    }
}
