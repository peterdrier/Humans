using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Feedback")]
public class FeedbackController : HumansControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        ITeamService teamService,
        IUserService userService,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
        : base(userService)
    {
        _feedbackService = feedbackService;
        _teamService = teamService;
        _userService = userService;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    /// Resolves active-approved humans into <see cref="AssigneeOption"/>
    /// rows for the assignee dropdowns. Replaces the deleted
    /// <c>IProfileService.GetFilteredHumansAsync(null, "Active")</c> path:
    /// person-search consolidation moved that surface to
    /// <c>IUserService.SearchUsersAsync</c>, which is for text search, not
    /// population queries. Population goes through the UserInfo snapshot +
    /// <c>IUserService.GetByIdsAsync</c> primitives.
    /// </summary>
    private async Task<List<AssigneeOption>> GetActiveAssigneeOptionsAsync(CancellationToken ct = default)
    {
        var options = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.IsActive)
            .Select(u => new AssigneeOption { Id = u.Id, DisplayName = u.BurnerName })
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return options;
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
        IReadOnlyList<TeamInfo> teamOptions = [];

        if (isAdmin)
        {
            teamOptions = (await _teamService.GetTeamsAsync()).Values
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            assigneeOptions = await GetActiveAssigneeOptionsAsync();
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
                ReporterName = r.ReporterName,
                ReporterUserId = r.UserId,
                PageUrl = r.PageUrl,
                CreatedAt = r.CreatedAt.ToDateTimeUtc(),
                HasScreenshot = r.ScreenshotStoragePath is not null,
                MessageCount = r.Messages.Count,
                GitHubIssueNumber = r.GitHubIssueNumber,
                NeedsReply = r.NeedsReply,
                AssignedToName = r.AssignedToName,
                AssignedToUserId = r.AssignedToUserId,
                AssignedToTeamName = r.AssignedToTeamName,
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

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        var report = await _feedbackService.GetFeedbackByIdForViewerAsync(id, user.Id, isAdmin);
        if (report is null) return NotFound();

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
            var roles = User.Claims
                .Where(c => string.Equals(c.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.Ordinal))
                .Select(c => c.Value)
                .ToList();

            await _feedbackService.SubmitUserFeedbackAsync(
                user.Id, model.Category, model.Description,
                model.PageUrl, model.UserAgent, roles,
                model.Screenshot);

            var successMsg = _localizer["Feedback_Submitted"].Value;
            if (isAjax) return Json(new { success = true, message = successMsg });
            SetSuccess(successMsg);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Feedback submission failed for user {UserId}", user.Id);
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

        if (!ModelState.IsValid)
        {
            SetError("Message is required.");
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            await _feedbackService.PostMessageAsync(id, user.Id, model.Content, RoleChecks.IsFeedbackAdmin(User));
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

    private static FeedbackDetailViewModel MapDetailViewModel(FeedbackReportInfo report, bool isAdmin)
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
            ReporterName = report.ReporterName,
            ReporterUserId = report.UserId,
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByName,
            IsAdmin = isAdmin,
            AssignedToUserId = report.AssignedToUserId,
            AssignedToName = report.AssignedToName,
            AssignedToTeamId = report.AssignedToTeamId,
            AssignedToTeamName = report.AssignedToTeamName,
            Messages = report.Messages.Select(m => new FeedbackMessageViewModel
            {
                Id = m.Id,
                SenderName = m.SenderName ?? "Unknown",
                SenderUserId = m.SenderUserId,
                Content = m.Content,
                CreatedAt = m.CreatedAt.ToDateTimeUtc(),
                IsReporter = m.SenderUserId.HasValue && m.SenderUserId == report.UserId
            }).ToList()
        };
    }

    private async Task PopulateAssignmentOptionsAsync(FeedbackDetailViewModel viewModel)
    {
        var teamsById = await _teamService.GetTeamsAsync();
        var teamOptions = teamsById.Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        // Include currently assigned team even if inactive, to prevent silent clearing.
        // The (inactive) suffix is rendered conditionally in the view.
        if (viewModel.AssignedToTeamId.HasValue &&
            teamOptions.All(t => t.Id != viewModel.AssignedToTeamId.Value)
            && teamsById.TryGetValue(viewModel.AssignedToTeamId.Value, out var inactiveTeam))
        {
            teamOptions.Insert(0, inactiveTeam);
        }

        viewModel.TeamOptions = teamOptions;

        viewModel.AssigneeOptions = await GetActiveAssigneeOptionsAsync();

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
