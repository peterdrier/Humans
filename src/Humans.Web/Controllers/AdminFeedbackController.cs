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

    public AdminFeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _feedbackService = feedbackService;
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
                HasScreenshot = r.ScreenshotStoragePath != null
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report == null)
            return NotFound();

        var viewModel = new FeedbackDetailViewModel
        {
            Id = report.Id,
            Category = report.Category,
            Status = report.Status,
            Description = report.Description,
            PageUrl = report.PageUrl,
            UserAgent = report.UserAgent,
            ScreenshotUrl = report.ScreenshotStoragePath != null ? $"/{report.ScreenshotStoragePath}" : null,
            ReporterName = report.User.DisplayName,
            ReporterUserId = report.UserId,
            AdminNotes = report.AdminNotes,
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            AdminResponseSentAt = report.AdminResponseSentAt?.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByUser?.DisplayName
        };

        return View(viewModel);
    }

    [HttpPost("{id}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateFeedbackStatusModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing != null) return userMissing;

        await _feedbackService.UpdateStatusAsync(id, model.Status, user.Id);
        SetSuccess("Status updated.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id}/Notes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateNotes(Guid id, UpdateFeedbackNotesModel model)
    {
        await _feedbackService.UpdateAdminNotesAsync(id, model.Notes);
        SetSuccess("Notes saved.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetGitHubIssueModel model)
    {
        await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
        SetSuccess("GitHub issue linked.");
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

        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing != null) return userMissing;

        await _feedbackService.SendResponseAsync(id, model.Message, user.Id);
        SetSuccess("Response sent.");
        return RedirectToAction(nameof(Detail), new { id });
    }
}
