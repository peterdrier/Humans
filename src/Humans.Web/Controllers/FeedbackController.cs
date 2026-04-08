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
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        ITeamService teamService,
        IProfileService profileService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
        : base(userManager)
    {
        _feedbackService = feedbackService;
        _teamService = teamService;
        _profileService = profileService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        FeedbackStatus? status, FeedbackCategory? category, Guid? reporterUserId,
        Guid? assignedTo, Guid? team, bool unassigned, Guid? selected)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        Guid? reporterFilter = isAdmin ? reporterUserId : user.Id;

        var reports = await _feedbackService.GetFeedbackListAsync(
            status, category, reporterFilter,
            assignedToUserId: isAdmin ? assignedTo : null,
            assignedToTeamId: isAdmin ? team : null,
            unassignedOnly: isAdmin && unassigned ? true : null);

        var assigneeOptions = new List<AssigneeOption>();
        var teamOptions = new List<TeamOptionDto>();

        if (isAdmin)
        {
            teamOptions = (await _teamService.GetActiveTeamOptionsAsync()).ToList();

            var humans = await _profileService.GetFilteredHumansAsync(null, "Active");
            assigneeOptions = humans
                .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(h => new AssigneeOption { Id = h.UserId, DisplayName = h.DisplayName })
                .ToList();
        }

        var reporters = new List<ReporterDropdownItem>();
        if (isAdmin)
        {
            var distinctReporters = await _feedbackService.GetDistinctReportersAsync();
            reporters = distinctReporters.Select(r => new ReporterDropdownItem
            {
                UserId = r.UserId,
                DisplayName = r.DisplayName,
                Count = r.Count
            }).ToList();
        }

        var viewModel = new FeedbackPageViewModel
        {
            StatusFilter = status,
            CategoryFilter = category,
            ReporterFilter = isAdmin ? reporterUserId : null,
            Reporters = reporters,
            AssignedToFilter = assignedTo,
            TeamFilter = team,
            UnassignedFilter = unassigned,
            IsAdmin = isAdmin,
            SelectedReportId = selected,
            CurrentUserId = user.Id,
            AssigneeOptions = assigneeOptions,
            TeamOptions = teamOptions,
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
                    (r.Status == FeedbackStatus.Open && !r.LastAdminMessageAt.HasValue),
                AssignedToName = r.AssignedToUser?.DisplayName,
                AssignedToUserId = r.AssignedToUserId,
                AssignedToTeamName = r.AssignedToTeam?.Name,
                AssignedToTeamId = r.AssignedToTeamId
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

        if (isAdmin)
        {
            await PopulateAssignmentOptionsAsync(viewModel);
        }

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
    [Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]
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

    [HttpPost("{id}/Assignment")]
    [Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignment(Guid id, UpdateFeedbackAssignmentModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await _feedbackService.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, user.Id);
            SetSuccess("Assignment updated.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update assignment for feedback {FeedbackId}", id);
            SetError("Failed to update assignment.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]
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
            AssignedToUserId = report.AssignedToUserId,
            AssignedToName = report.AssignedToUser?.DisplayName,
            AssignedToTeamId = report.AssignedToTeamId,
            AssignedToTeamName = report.AssignedToTeam?.Name,
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

    private async Task PopulateAssignmentOptionsAsync(FeedbackDetailViewModel viewModel)
    {
        viewModel.TeamOptions = (await _teamService.GetActiveTeamOptionsAsync()).ToList();

        // Include currently assigned team even if inactive, to prevent silent clearing
        if (viewModel.AssignedToTeamId.HasValue &&
            viewModel.TeamOptions.All(t => t.Id != viewModel.AssignedToTeamId.Value))
        {
            var inactiveTeam = await _teamService.GetTeamByIdAsync(viewModel.AssignedToTeamId.Value);
            if (inactiveTeam is not null)
            {
                viewModel.TeamOptions.Insert(0,
                    new TeamOptionDto(inactiveTeam.Id, $"{inactiveTeam.Name} (inactive)"));
            }
        }

        var humans = await _profileService.GetFilteredHumansAsync(null, "Active");
        viewModel.AssigneeOptions = humans
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(h => new AssigneeOption { Id = h.UserId, DisplayName = h.DisplayName })
            .ToList();

        // Include currently assigned human even if inactive, to prevent silent clearing
        if (viewModel.AssignedToUserId.HasValue &&
            viewModel.AssigneeOptions.All(a => a.Id != viewModel.AssignedToUserId.Value))
        {
            var label = viewModel.AssignedToName ?? "Unknown";
            viewModel.AssigneeOptions.Insert(0,
                new AssigneeOption { Id = viewModel.AssignedToUserId.Value, DisplayName = $"{label} (inactive)" });
        }
    }
}
