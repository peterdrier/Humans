using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Feedback.Domain;
using Humans.Feedback.Models;
using Humans.Feedback.Services;
using Humans.Feedback.Contracts;
using Humans.Base.Models;
using Humans.Teams.Contracts;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Feedback.Controllers;

/// <summary>
/// Read-and-triage surface for the retired Feedback section
/// (nobodies-collective/Humans#977). Feedback no longer accepts new reports —
/// Issues superseded it — so every remaining action is full-Admin only. There is
/// deliberately no reporter-facing view and no creation route.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Feedback")]
internal sealed class FeedbackController(
    FeedbackService feedbackService,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    ILogger<FeedbackController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

    /// <summary>
    /// Resolves active-approved humans into <see cref="AssigneeOption"/>
    /// rows for the assignee dropdowns. Replaces the deleted
    /// <c>IProfileService.GetFilteredHumansAsync(null, "Active")</c> path:
    /// person-search consolidation moved that surface to
    /// <c>IUserService.SearchUsersAsync</c>, which is for text search, not
    /// population queries. Population goes through the UserInfo snapshot +
    /// <c>IUserServiceRead.GetAllUserInfosAsync</c> primitive.
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

        var reports = await feedbackService.GetFeedbackListAsync(
            status, category, reporterUserId,
            assignedToUserId: assignedTo,
            assignedToTeamId: team,
            unassignedOnly: unassigned ? true : null);

        var teamOptions = (await teamService.GetTeamsAsync()).Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var assigneeOptions = await GetActiveAssigneeOptionsAsync();

        var distinctReporters = await feedbackService.GetDistinctReportersAsync();
        var reporters = distinctReporters.Select(r => new ReporterDropdownItem
        {
            UserId = r.UserId,
            DisplayName = r.DisplayName,
            Count = r.Count
        }).ToList();

        var viewModel = new FeedbackPageViewModel
        {
            StatusFilter = status,
            CategoryFilter = category,
            ReporterFilter = reporterUserId,
            Reporters = reporters,
            AssignedToFilter = assignedTo,
            TeamFilter = team,
            UnassignedFilter = unassigned,
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
        var report = await feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var viewModel = MapDetailViewModel(report);
        await PopulateAssignmentOptionsAsync(viewModel);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
        {
            return PartialView("_Detail", viewModel);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
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
            await feedbackService.PostMessageAsync(id, user.Id, model.Content);
            SetSuccess("Message posted.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            SetError("Failed to post message.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateFeedbackStatusModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await feedbackService.UpdateStatusAsync(id, model.Status, user.Id);
            SetSuccess("Status updated.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            SetError("Failed to update status.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Assignment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignment(Guid id, UpdateFeedbackAssignmentModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await feedbackService.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, user.Id);
            SetSuccess("Assignment updated.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update assignment for feedback {FeedbackId}", id);
            SetError("Failed to update assignment.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetGitHubIssueModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber, user.Id);
            SetSuccess("GitHub issue linked.");
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            SetError("Failed to link GitHub issue.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private static FeedbackDetailViewModel MapDetailViewModel(FeedbackReportInfo report)
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
            ReporterUserId = report.UserId,
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByName,
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
        var teamsById = await teamService.GetTeamsAsync();
        var teamOptions = teamsById.Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        // Include currently assigned team even if inactive, to prevent silent clearing.
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
