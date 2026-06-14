using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Issues;
using Humans.Domain.Constants;

namespace Humans.Web.ViewComponents;

// No IMemoryCache here (memory/code/viewcomponent-no-cache.md). Each count comes from
// its owning service: feedback/voting/issues are cached inside those services; the review
// count reads the already-cache-served UserInfo store, so it needs no extra cache.
public class NavBadgesViewComponent(
    IAdminDashboardService adminDashboardService,
    IApplicationServiceRead applicationDecisionService,
    IFeedbackService feedbackService,
    IIssuesService issuesService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string queue)
    {
        int count;
        if (string.Equals(queue, "voting", StringComparison.OrdinalIgnoreCase))
        {
            count = await GetPerUserVotingCountAsync();
        }
        else if (string.Equals(queue, "review", StringComparison.OrdinalIgnoreCase))
        {
            count = await adminDashboardService.GetPendingReviewCountAsync();
        }
        else if (string.Equals(queue, "issues", StringComparison.OrdinalIgnoreCase))
        {
            count = await GetPerUserIssuesCountAsync();
        }
        else
        {
            count = await feedbackService.GetActionableCountAsync();
        }

        return View(count);
    }

    private async Task<int> GetPerUserVotingCountAsync()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var currentUserId))
            return 0;

        return await applicationDecisionService.GetUnvotedApplicationCountAsync(currentUserId);
    }

    /// <summary>
    /// Per-user count of issues actionable by the viewer:
    /// Open + Triage in sections their roles own, plus their own non-terminal
    /// issues. Admins see the global open count. Claims-first role lookup —
    /// see coding-rules.md. Caching + per-user invalidation live in
    /// <c>IssuesService</c> per <c>memory/code/viewcomponent-no-cache.md</c>.
    /// </summary>
    private async Task<int> GetPerUserIssuesCountAsync()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var currentUserId))
            return 0;

        var roles = UserClaimsPrincipal.Claims
            .Where(c => string.Equals(c.Type, ClaimTypes.Role, StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToList();
        var isAdmin = UserClaimsPrincipal.IsInRole(RoleNames.Admin);

        return await issuesService.GetActionableCountForViewerAsync(
            currentUserId, roles, isAdmin);
    }
}
