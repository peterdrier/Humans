using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Feedback")]
public class FeedbackController : HumansControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
        : base(userManager)
    {
        _feedbackService = feedbackService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        FeedbackStatus? status, FeedbackCategory? category, Guid? selected)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        Guid? reporterFilter = isAdmin ? null : user.Id;

        var reports = await _feedbackService.GetFeedbackListAsync(status, category, reporterFilter);

        var viewModel = new FeedbackPageViewModel
        {
            StatusFilter = status,
            CategoryFilter = category,
            IsAdmin = isAdmin,
            SelectedReportId = selected,
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
                HasScreenshot = r.ScreenshotStoragePath is not null,
                MessageCount = r.Messages.Count,
                GitHubIssueNumber = r.GitHubIssueNumber,
                NeedsReply = (r.LastReporterMessageAt.HasValue &&
                    (!r.LastAdminMessageAt.HasValue || r.LastReporterMessageAt > r.LastAdminMessageAt)) ||
                    (r.Status == FeedbackStatus.Open && !r.LastAdminMessageAt.HasValue)
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        if (!isAdmin && report.UserId != user.Id) return NotFound();

        var viewModel = MapDetailViewModel(report, isAdmin);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
        {
            return PartialView("_Detail", viewModel);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitFeedbackViewModel model)
    {
        var isAjax = Request.Headers.XRequestedWith == "XMLHttpRequest";

        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null)
        {
            return isAjax ? Unauthorized() : userMissing;
        }

        if (!ModelState.IsValid)
        {
            var errorMsg = _localizer["Feedback_Error"].Value;
            if (isAjax) return Json(new { success = false, message = errorMsg });
            SetError(errorMsg);
            return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
        }

        try
        {
            var roles = await UserManager.GetRolesAsync(user);
            var additionalContext = roles.Count > 0 ? string.Join(", ", roles.Order(StringComparer.Ordinal)) : null;

            await _feedbackService.SubmitFeedbackAsync(
                user.Id, model.Category, model.Description,
                model.PageUrl, model.UserAgent, additionalContext,
                model.Screenshot);

            var successMsg = _localizer["Feedback_Submitted"].Value;
            if (isAjax) return Json(new { success = true, message = successMsg });
            SetSuccess(successMsg);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Feedback submission failed for user {UserId}", user.Id);
            var errorMsg = _localizer["Feedback_Error"].Value;
            if (isAjax) return Json(new { success = false, message = errorMsg });
            SetError(errorMsg);
        }

        return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
    }

    [HttpPost("{id}/Message")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostMessage(Guid id, PostFeedbackMessageModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        if (!isAdmin && report.UserId != user.Id) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Message is required.");
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            await _feedbackService.PostMessageAsync(id, user.Id, model.Content, isAdmin);
            SetSuccess("Message posted.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            SetError("Failed to post message.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Status")]
    [Authorize(Roles = RoleGroups.FeedbackAdminOrAdmin)]
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
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            SetError("Failed to update status.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [Authorize(Roles = RoleGroups.FeedbackAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            SetSuccess("GitHub issue linked.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            SetError("Failed to link GitHub issue.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private static FeedbackDetailViewModel MapDetailViewModel(FeedbackReport report, bool isAdmin)
    {
        return new FeedbackDetailViewModel
        {
            Id = report.Id,
            Category = report.Category,
            Status = report.Status,
            Description = report.Description,
            PageUrl = report.PageUrl,
            UserAgent = report.UserAgent,
            AdditionalContext = report.AdditionalContext,
            ScreenshotUrl = report.ScreenshotStoragePath is not null
                ? $"/{report.ScreenshotStoragePath}" : null,
            ReporterName = report.User.DisplayName,
            ReporterUserId = report.UserId,
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByUser?.DisplayName,
            IsAdmin = isAdmin,
            Messages = report.Messages.Select(m => new FeedbackMessageViewModel
            {
                Id = m.Id,
                SenderName = m.SenderUser?.DisplayName ?? "Unknown",
                SenderUserId = m.SenderUserId,
                Content = m.Content,
                CreatedAt = m.CreatedAt.ToDateTimeUtc(),
                IsReporter = m.SenderUserId.HasValue && m.SenderUserId == report.UserId
            }).ToList()
        };
    }
}
