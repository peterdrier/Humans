using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
    /// <summary>
    /// Resolves active humans into <see cref="AssigneeOption"/> rows for the
    /// assignee dropdowns. Population query, not text search — it reads the
    /// UserInfo snapshot via <c>GetAllUserInfosAsync</c>, never
    /// <c>SearchUsersAsync</c>.
    /// </summary>
    private async Task<List<AssigneeOption>> GetActiveAssigneeOptionsAsync(CancellationToken ct = default)
    {
        var options = (await UserService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.IsActive)
            .Select(u => new AssigneeOption { Id = u.Id, DisplayName = u.BurnerName })
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return options;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        FeedbackStatus? status, FeedbackCategory? category, Guid? reporterUserId,
        Guid? assignedTo, Guid? team, bool unassigned, Guid? selected, CancellationToken ct)
    {
        var (userMissing, user) = await RequireCurrentUserAsync(ct);
        if (userMissing is not null) return userMissing;

        var reports = await feedbackService.GetFeedbackListAsync(
            status, category, reporterUserId,
            assignedToUserId: assignedTo,
            assignedToTeamId: team,
            unassignedOnly: unassigned ? true : null,
            cancellationToken: ct);

        var teamOptions = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var assigneeOptions = await GetActiveAssigneeOptionsAsync(ct);

        var distinctReporters = await feedbackService.GetDistinctReportersAsync(ct);
        var reporterUsers = await UserService.GetUserInfosAsync(
            distinctReporters.Select(r => r.UserId).ToList(), ct);
        var reporters = distinctReporters
            .Select(r => (r.UserId, r.Count, Name: reporterUsers.TryGetValue(r.UserId, out var info)
                ? info.BurnerName : r.UserId.ToString()))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SelectListItem
            {
                Value = r.UserId.ToString(),
                Text = $"{r.Name} ({r.Count})",
                Selected = reporterUserId == r.UserId
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
                Description = r.Description.Length > 100
                    ? r.Description[..(char.IsHighSurrogate(r.Description[99]) && char.IsLowSurrogate(r.Description[100]) ? 99 : 100)] + "..."
                    : r.Description,
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
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var report = await feedbackService.GetFeedbackByIdAsync(id, ct);
        if (report is null) return NotFound();

        var viewModel = MapDetailViewModel(report);
        await PopulateAssignmentOptionsAsync(viewModel, ct);

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
            var result = await feedbackService.PostMessageAsync(id, user.Id, model.Content);
            if (!result.Found) return NotFound();
            if (!result.Succeeded) SetError(result.Rejection!);
            else SetSuccess("Message posted.");
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

            if (!ModelState.IsValid)
            {
                SetError("Invalid status.");
                return RedirectToAction(nameof(Index), new { selected = id });
            }

            var result = await feedbackService.UpdateStatusAsync(id, model.Status, user.Id);
            if (!result.Found) return NotFound();
            if (!result.Succeeded) SetError(result.Rejection!);
            else SetSuccess("Status updated.");
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

            if (!ModelState.IsValid)
            {
                SetError("Invalid assignment.");
                return RedirectToAction(nameof(Index), new { selected = id });
            }

            var result = await feedbackService.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, user.Id);
            if (!result.Found) return NotFound();
            if (!result.Succeeded) SetError(result.Rejection!);
            else SetSuccess("Assignment updated.");
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

            if (!ModelState.IsValid)
            {
                SetError("Invalid issue number.");
                return RedirectToAction(nameof(Index), new { selected = id });
            }

            var result = await feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber, user.Id);
            if (!result.Found) return NotFound();
            if (!result.Succeeded) SetError(result.Rejection!);
            else SetSuccess("GitHub issue linked.");
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

    private async Task PopulateAssignmentOptionsAsync(FeedbackDetailViewModel viewModel, CancellationToken ct = default)
    {
        var teamsById = await teamService.GetTeamsAsync(ct);
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

        viewModel.AssigneeOptions = await GetActiveAssigneeOptionsAsync(ct);

        // Same for the assignee.
        if (viewModel.AssignedToUserId.HasValue &&
            viewModel.AssigneeOptions.All(a => a.Id != viewModel.AssignedToUserId.Value))
        {
            var label = viewModel.AssignedToName ?? "Unknown";
            viewModel.AssigneeOptions.Insert(0,
                new AssigneeOption { Id = viewModel.AssignedToUserId.Value, DisplayName = $"{label} (inactive)" });
        }
    }
}
