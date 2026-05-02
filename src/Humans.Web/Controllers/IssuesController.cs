using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Helpers;
using Humans.Web.Models;

// Issue / IssueComment cross-domain nav properties (Reporter, Assignee,
// ResolvedByUser, SenderUser) are [Obsolete] — IssuesService stitches them in
// memory from IUserService so controllers can read them for view-model shaping.
// Nav-strip follow-up tracked in design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Authorize]
[Route("Issues")]
public class IssuesController : HumansControllerBase
{
    private readonly IIssuesService _issues;
    private readonly IAuthorizationService _authorization;
    private readonly IProfileService _profiles;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<IssuesController> _logger;

    public IssuesController(
        IIssuesService issues,
        IAuthorizationService authorization,
        IProfileService profiles,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<IssuesController> logger)
        : base(userManager)
    {
        _issues = issues;
        _authorization = authorization;
        _profiles = profiles;
        _localizer = localizer;
        _logger = logger;
    }

    // Roles must come from claims (the RoleAssignment → claims-transformation
    // path), NOT from UserManager.GetRolesAsync (which only returns Identity-DB
    // roles and misses RoleAssignment-driven roles like CampAdmin). Mirrors
    // NavBadgesViewComponent so the badge count and the list always agree.
    private List<string> ClaimsRoles() => User.Claims
        .Where(c => string.Equals(c.Type, ClaimTypes.Role, StringComparison.Ordinal))
        .Select(c => c.Value)
        .ToList();

    [HttpGet("")]
    public async Task<IActionResult> Index(
        IssueViewMode? view,
        IssueCategory? category,
        string? section,
        Guid? reporter,
        string? search,
        Guid? selected)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var roles = ClaimsRoles();
        var isAdmin = User.IsInRole(RoleNames.Admin);
        var viewMode = view ?? IssueViewMode.All;

        // "Open" expands to the non-terminal set so the count matches the
        // nav badge's actionable definition; "Closed" inverts to terminal.
        // "Mine" forces ReporterUserId to the current user — allowed for any
        // user since filtering on your own reports is never a privacy escalation.
        var statuses = viewMode switch
        {
            IssueViewMode.Open => new[] { IssueStatus.Triage, IssueStatus.Open, IssueStatus.InProgress },
            IssueViewMode.Closed => new[] { IssueStatus.Resolved, IssueStatus.WontFix, IssueStatus.Duplicate },
            _ => null
        };

        // Admin reporter dropdown is independent of the view-mode buttons.
        // Non-admins may only filter by their own ID (the Mine button); the
        // dropdown is never rendered for them.
        Guid? reporterFilter = viewMode == IssueViewMode.Mine
            ? user.Id
            : (isAdmin ? reporter : null);

        var filter = new IssueListFilter(
            Statuses: statuses,
            Categories: category.HasValue ? new[] { category.Value } : null,
            Sections: !string.IsNullOrWhiteSpace(section) ? new string?[] { section } : null,
            ReporterUserId: reporterFilter,
            AssigneeUserId: null,
            SearchText: !string.IsNullOrWhiteSpace(search) ? search : null,
            Limit: 200);

        var issues = await _issues.GetIssueListAsync(filter, user.Id, roles, isAdmin);

        // Section dropdown: Admin sees all known sections; non-admins see the
        // sections their roles own (so they only filter inside their own queue).
        var allowedSections = isAdmin
            ? IssueSectionRouting.AllKnownSections
            : IssueSectionRouting.SectionsForRoles(roles).ToList();

        var sectionOptions = allowedSections
            .Select(s => new SectionOption { Section = s, Label = AreaLabelMap.LabelFor(s) })
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reporterOptions = new List<ReporterDropdownItem>();
        if (isAdmin)
        {
            var distinct = await _issues.GetDistinctReportersAsync();
            reporterOptions = distinct
                .Select(r => new ReporterDropdownItem
                {
                    UserId = r.UserId,
                    DisplayName = r.DisplayName,
                    Count = r.Count
                })
                .ToList();
        }

        var rows = issues.Select(MapListItem).ToList();

        var vm = new IssuePageViewModel
        {
            Issues = rows,
            View = viewMode,
            CategoryFilter = category,
            SectionFilter = section,
            ReporterFilter = isAdmin ? reporter : null,
            SearchText = search,
            CurrentUserId = user.Id,
            IsAdmin = isAdmin,
            SelectedIssueId = selected,
            SectionOptions = sectionOptions,
            Reporters = reporterOptions,
            OpenCount = rows.Count(r => !r.Status.IsTerminal()),
            TotalCount = rows.Count
        };

        return View(vm);
    }

    [HttpGet("New")]
    public IActionResult New(string? section)
    {
        var model = new SubmitIssueViewModel
        {
            Section = section,
            Category = IssueCategory.Bug
        };
        return View(model);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitIssueViewModel model)
    {
        var isAjax = Request.Headers.XRequestedWith == "XMLHttpRequest";

        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null)
        {
            return isAjax ? Unauthorized() : userMissing;
        }

        if (!ModelState.IsValid)
        {
            if (isAjax) return BadRequest(ModelState);
            SetError("Please fill in the required fields.");
            return View("New", model);
        }

        var section = model.Section ?? IssueSectionInference.FromPath(model.PageUrl);

        try
        {
            var roles = ClaimsRoles();
            var additionalContextParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(model.AdditionalContext))
                additionalContextParts.Add(model.AdditionalContext);
            if (roles.Count > 0)
                additionalContextParts.Add($"roles: {string.Join(", ", roles.Order(StringComparer.Ordinal))}");
            var additionalContext = additionalContextParts.Count > 0
                ? string.Join(" | ", additionalContextParts)
                : null;
            // additional_context column is varchar(2000); the user input is bounded
            // by [StringLength(2000)] but the roles suffix can push it past the limit.
            if (additionalContext is { Length: > 2000 })
                additionalContext = additionalContext[..2000];

            var issue = await _issues.SubmitIssueAsync(
                reporterUserId: user.Id,
                category: model.Category,
                title: model.Title,
                description: model.Description,
                section: section,
                pageUrl: model.PageUrl,
                userAgent: model.UserAgent,
                additionalContext: additionalContext,
                screenshot: model.Screenshot,
                dueDate: model.DueDate);

            if (isAjax) return Json(new { id = issue.Id });

            SetSuccess("Issue filed.");
            return RedirectToAction(nameof(Index), new { selected = issue.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit issue for user {UserId}", user.Id);
            if (isAjax) return StatusCode(500, new { error = "Failed to file issue" });
            SetError("Failed to file issue.");
            return View("New", model);
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id, bool partial = false)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var isPartial = partial || Request.Headers.XRequestedWith == "XMLHttpRequest";
        var issue = await _issues.GetIssueByIdAsync(id);

        // Treat "not found" and "no access" identically so we don't leak which
        // GUIDs exist. For partial requests (the JS that paints the right pane
        // on /Issues?selected=X), return a small inline notice so the panel
        // doesn't get the framework's full-layout 404 rendered into it. For a
        // direct nav, drop back to the Index without the selected param so the
        // URL doesn't keep advertising the bad ID.
        var canHandle = issue is not null
            && (await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)).Succeeded;
        var isReporter = issue is not null && issue.ReporterUserId == user.Id;

        if (issue is null || (!canHandle && !isReporter))
        {
            return isPartial
                ? PartialView("_DetailUnavailable")
                : RedirectToAction(nameof(Index));
        }

        var thread = await _issues.GetThreadAsync(id);
        var vm = MapDetailViewModel(issue, thread, isHandler: canHandle, isReporter: isReporter);

        if (canHandle)
        {
            await PopulateAssigneeOptionsAsync(vm);
        }

        if (isPartial)
        {
            return PartialView("_Detail", vm);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private async Task PopulateAssigneeOptionsAsync(IssueDetailViewModel vm)
    {
        var humans = await _profiles.GetFilteredHumansAsync(null, "Active");
        vm.AssigneeOptions = humans
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(h => new AssigneeOption { Id = h.UserId, DisplayName = h.DisplayName })
            .ToList();

        // If the current assignee isn't in the active list (left org, etc.),
        // surface them anyway so the dropdown doesn't silently un-assign.
        if (vm.AssigneeUserId.HasValue &&
            vm.AssigneeOptions.All(a => a.Id != vm.AssigneeUserId.Value))
        {
            vm.AssigneeOptions.Insert(0, new AssigneeOption
            {
                Id = vm.AssigneeUserId.Value,
                DisplayName = (vm.AssigneeName ?? "Unknown") + " (inactive)"
            });
        }
    }

    [HttpPost("{id}/Comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostComment(Guid id, PostIssueCommentModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var canHandle = (await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)).Succeeded;
        var isReporter = issue.ReporterUserId == user.Id;
        if (!canHandle && !isReporter) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Comment is required.");
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            await _issues.PostCommentAsync(id, user.Id, model.Content, senderIsReporter: isReporter);

            // "Comment & mark resolved" — only handlers can mark resolved.
            if (model.ResolveOnPost && canHandle && !issue.Status.IsTerminal())
            {
                await _issues.UpdateStatusAsync(id, IssueStatus.Resolved, user.Id);
            }

            SetSuccess("Comment posted.");
        }
        catch (InvalidOperationException)
        {
            // Race: issue existed at the null-check above but was deleted
            // before the mutation reached the service.
            _logger.LogWarning("Issue {IssueId} not found during PostComment (deleted in race)", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post comment on issue {IssueId}", id);
            SetError("Failed to post comment.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateIssueStatusModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _issues.UpdateStatusAsync(id, model.Status, user.Id);
            SetSuccess("Status updated.");
        }
        catch (InvalidOperationException)
        {
            // Race: issue existed at the null-check above but was deleted
            // before the mutation reached the service.
            _logger.LogWarning("Issue {IssueId} not found during UpdateStatus (deleted in race)", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update issue {IssueId} status", id);
            SetError("Failed to update status.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Assignee")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignee(Guid id, UpdateIssueAssigneeModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _issues.UpdateAssigneeAsync(id, model.AssigneeUserId, user.Id);
            SetSuccess("Assignee updated.");
        }
        catch (InvalidOperationException)
        {
            // Race: issue existed at the null-check above but was deleted
            // before the mutation reached the service.
            _logger.LogWarning("Issue {IssueId} not found during UpdateAssignee (deleted in race)", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update assignee on issue {IssueId}", id);
            SetError("Failed to update assignee.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Section")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSection(Guid id, UpdateIssueSectionModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _issues.UpdateSectionAsync(id, model.Section, user.Id);
            SetSuccess("Section updated.");
        }
        catch (InvalidOperationException ex)
        {
            // UpdateSectionAsync throws for both "not found" and "section is
            // not editable on a terminal issue" — surface the message so a
            // handler trying to re-route a closed issue sees the actual reason
            // rather than a silent 404. Both are expected user-driven problems;
            // log at Warning so they stay visible in the prod log viewer
            // (memory/code/always-log-problems.md).
            _logger.LogWarning("Issue {IssueId} UpdateSection rejected: {Reason}", id, ex.Message);
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update section on issue {IssueId}", id);
            SetError("Failed to update section.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetIssueGitHubIssueModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await _authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        try
        {
            await _issues.SetGitHubIssueNumberAsync(id, model.GitHubIssueNumber, user.Id);
            SetSuccess("GitHub issue linked.");
        }
        catch (InvalidOperationException)
        {
            // Race: issue existed at the null-check above but was deleted
            // before the mutation reached the service.
            _logger.LogWarning("Issue {IssueId} not found during SetGitHubIssue (deleted in race)", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for issue {IssueId}", id);
            SetError("Failed to link GitHub issue.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private static IssueListItemViewModel MapListItem(Issue i) => new()
    {
        Id = i.Id,
        Status = i.Status,
        Category = i.Category,
        Section = i.Section,
        AreaLabel = AreaLabelMap.LabelFor(i.Section),
        Title = i.Title,
        ReporterName = i.Reporter?.DisplayName ?? "Unknown",
        ReporterUserId = i.ReporterUserId,
        LastUpdate = i.UpdatedAt.ToDateTimeUtc(),
        CommentCount = i.Comments.Count,
        AssigneeUserId = i.AssigneeUserId,
        AssigneeName = i.Assignee?.DisplayName,
        GitHubIssueNumber = i.GitHubIssueNumber
    };

    private static IssueDetailViewModel MapDetailViewModel(
        Issue i, IReadOnlyList<IssueThreadEvent> thread, bool isHandler, bool isReporter)
    {
        return new IssueDetailViewModel
        {
            Id = i.Id,
            Status = i.Status,
            Category = i.Category,
            Section = i.Section,
            AreaLabel = AreaLabelMap.LabelFor(i.Section),
            Title = i.Title,
            Description = i.Description,
            PageUrl = i.PageUrl,
            UserAgent = i.UserAgent,
            AdditionalContext = i.AdditionalContext,
            ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
            ReporterName = i.Reporter?.DisplayName ?? "Unknown",
            ReporterUserId = i.ReporterUserId,
            AssigneeName = i.Assignee?.DisplayName,
            AssigneeUserId = i.AssigneeUserId,
            GitHubIssueNumber = i.GitHubIssueNumber,
            DueDate = i.DueDate,
            CreatedAt = i.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = i.ResolvedByUser?.DisplayName,
            IsHandler = isHandler,
            IsReporter = isReporter,
            Thread = thread.Select(e => e switch
            {
                IssueCommentEvent c => new IssueThreadEventViewModel
                {
                    Type = "comment",
                    At = c.At.ToDateTimeUtc(),
                    ActorUserId = c.ActorUserId,
                    ActorName = c.ActorDisplayName,
                    Content = c.Content,
                    ActorIsReporter = c.ActorIsReporter
                },
                IssueAuditEvent a => new IssueThreadEventViewModel
                {
                    Type = "audit",
                    At = a.At.ToDateTimeUtc(),
                    ActorUserId = a.ActorUserId,
                    ActorName = a.ActorDisplayName,
                    Description = a.Description
                },
                _ => throw new NotSupportedException($"Unknown thread event type {e.GetType().Name}")
            }).ToList()
        };
    }
}
