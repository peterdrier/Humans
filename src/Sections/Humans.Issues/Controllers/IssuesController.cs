using Humans.Base.Controllers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Issues.Authorization;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using Humans.Issues.Models;
using Humans.Issues.Services;
using Humans.Base.Models;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Issues.Controllers;

[Authorize]
[Route("Issues")]
internal sealed class IssuesController(
    IIssuesService issues,
    IAuthorizationService authorization,
    IUserServiceRead users,
    IssueSectionRouting routing,
    IStringLocalizer<IssuesResource> localizer,
    ILogger<IssuesController> logger) : HumansControllerBase(users)
{
    // Roles from claims (RoleAssignment → claims-transformation), NOT UserManager.GetRolesAsync (misses CampAdmin etc.).
    private List<string> ClaimsRoles() => User.Claims
        .Where(c => string.Equals(c.Type, ClaimTypes.Role, StringComparison.Ordinal))
        .Select(c => c.Value)
        .ToList();

    /// <summary>
    /// The browsing user as the service scopes them. The service refuses anything out of
    /// their reach on its own; the <see cref="IAuthorizationService"/> checks below stay
    /// because they shape the page and answer 403 where the service would answer 404.
    /// </summary>
    private IssueViewer ViewerFor(Guid userId) => new(userId, ClaimsRoles());

    [HttpGet("")]
    public async Task<IActionResult> Index(
        IssueViewMode? view,
        IssueCategory? category,
        string? section,
        Guid? reporter,
        string? search,
        Guid? selected)
    {
        var (userMissing, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var viewMode = view ?? IssueViewMode.All;

        // Open = non-terminal; Closed = terminal; Mine = ReporterUserId == current user.
        // Wider than the nav badge, which counts Open + Triage only.
        var statuses = viewMode switch
        {
            IssueViewMode.Open => new[] { IssueStatus.Triage, IssueStatus.Open, IssueStatus.InProgress },
            IssueViewMode.Closed => new[] { IssueStatus.Resolved, IssueStatus.WontFix, IssueStatus.Duplicate },
            _ => null
        };

        // Non-admin: reporter filter forced to self (Mine button). Admin dropdown is independent.
        Guid? reporterFilter = viewMode == IssueViewMode.Mine
            ? user.Id
            : (viewer.IsAdmin ? reporter : null);

        var filter = new IssueListFilter(
            Statuses: statuses,
            Categories: category.HasValue ? [category.Value] : null,
            Sections: !string.IsNullOrWhiteSpace(section) ? [section] : null,
            ReporterUserId: reporterFilter,
            AssigneeUserId: null,
            SearchText: !string.IsNullOrWhiteSpace(search) ? search : null,
            Limit: 200);

        var matches = await issues.GetIssueListAsync(filter, viewer, HttpContext.RequestAborted);

        // Section dropdown: Admin sees all known sections; non-admins see the
        // sections their roles own (so they only filter inside their own queue).
        var allowedSections = viewer.IsAdmin
            ? routing.AllKnownSections
            : routing.SectionsForRoles(viewer.Roles).ToList();

        var sectionOptions = allowedSections
            .Select(s => new SectionOption { Section = s, Label = AreaLabelMap.LabelFor(s, localizer) })
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var reporterOptions = new List<ReporterDropdownItem>();
        if (viewer.IsAdmin)
        {
            var distinct = await issues.GetDistinctReportersAsync(HttpContext.RequestAborted);
            reporterOptions = distinct
                .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new ReporterDropdownItem
                {
                    UserId = r.UserId,
                    DisplayName = r.DisplayName,
                    Count = r.Count
                })
                .ToList();
        }

        var rows = matches.Select(MapListItem).ToList();

        var vm = new IssuePageViewModel
        {
            Issues = rows,
            View = viewMode,
            CategoryFilter = category,
            SectionFilter = section,
            ReporterFilter = viewer.IsAdmin ? reporter : null,
            SearchText = search,
            IsAdmin = viewer.IsAdmin,
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
            SetError(localizer["Issue_ValidationFailed"].Value);
            return View("New", model);
        }

        var section = model.Section ?? IssueSectionInference.FromPath(model.PageUrl);

        try
        {
            var roles = ClaimsRoles();
            var issue = await issues.SubmitIssueAsync(
                reporterUserId: user.Id,
                category: model.Category,
                title: model.Title,
                description: model.Description,
                section: section,
                pageUrl: model.PageUrl,
                userAgent: model.UserAgent,
                additionalContext: model.AdditionalContext,
                screenshot: model.Screenshot,
                dueDate: model.DueDate,
                reporterRoles: roles);

            if (isAjax) return Json(new { id = issue.Id });

            SetSuccess(localizer["Issue_Submitted"].Value);
            return RedirectToAction(nameof(Index), new { selected = issue.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit issue for user {UserId}", user.Id);
            if (isAjax) return StatusCode(500, new { error = "Failed to file issue" });
            SetError(localizer["Issue_Error"].Value);
            return View("New", model);
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id, bool partial = false)
    {
        var (userMissing, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (userMissing is not null) return userMissing;

        var isPartial = partial || Request.Headers.XRequestedWith == "XMLHttpRequest";
        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer, HttpContext.RequestAborted);

        // "Not found" and "no access" indistinguishable. Partial → inline notice; full nav → redirect to Index.
        var canHandle = issue is not null
            && (await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)).Succeeded;
        var isReporter = issue is not null && issue.ReporterUserId == user.Id;

        if (issue is null || (!canHandle && !isReporter))
        {
            return isPartial
                ? PartialView("_DetailUnavailable")
                : RedirectToAction(nameof(Index));
        }

        var thread = await issues.GetThreadAsync(id, viewer, HttpContext.RequestAborted);
        var displayUsers = await GetIssueDisplayUsersAsync(issue, HttpContext.RequestAborted);
        var vm = MapDetailViewModel(issue, thread, displayUsers, isHandler: canHandle, isReporter: isReporter);

        if (canHandle)
        {
            await PopulateAssigneeOptionsAsync(vm, HttpContext.RequestAborted);
        }

        if (isPartial)
        {
            return PartialView("_Detail", vm);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private async Task PopulateAssigneeOptionsAsync(IssueDetailViewModel vm, CancellationToken ct)
    {
        var activeIds = (await UserService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToList();
        if (activeIds.Count == 0)
        {
            vm.AssigneeOptions = [];
        }
        else
        {
            var active = await UserService.GetUserInfosAsync(activeIds, ct);
            vm.AssigneeOptions = active.Values
                .OrderBy(u => u.BurnerName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new AssigneeOption { Id = u.Id, DisplayName = u.BurnerName })
                .ToList();
        }

        // If the current assignee isn't in the active list (left org, etc.),
        // surface them anyway so the dropdown doesn't silently un-assign. A stored
        // assignee id that has been merged away selects its survivor's option instead.
        if (vm.AssigneeUserId.HasValue &&
            vm.AssigneeOptions.All(a => a.Id != vm.AssigneeUserId.Value))
        {
            var inactiveInfo = await UserService.GetUserInfoAsync(vm.AssigneeUserId.Value, ct);
            if (inactiveInfo is not null && inactiveInfo.Id != vm.AssigneeUserId.Value)
                vm.AssigneeUserId = inactiveInfo.Id;
            if (vm.AssigneeOptions.All(a => a.Id != vm.AssigneeUserId.Value))
            {
                vm.AssigneeOptions.Insert(0, new AssigneeOption
                {
                    Id = vm.AssigneeUserId.Value,
                    DisplayName = (inactiveInfo?.BurnerName ?? "Unknown") + " (inactive)"
                });
            }
        }
    }

    [HttpPost("{id}/Comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostComment(Guid id, PostIssueCommentModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer);
        if (issue is null) return NotFound();

        var canHandle = (await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)).Succeeded;
        var isReporter = issue.ReporterUserId == user.Id;
        if (!canHandle && !isReporter) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError(localizer["Issue_Comment_PostFailed"].Value);
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            var result = await issues.PostCommentAsync(
                id,
                viewer,
                user.Id,
                model.Content,
                resolveOnPost: model.ResolveOnPost && canHandle);

            if (result.NotFound) return NotFound();
            if (result.Comment is not null) SetSuccess(localizer["Issue_Comment_Posted"].Value);
            else SetError(localizer["Issue_Comment_PostFailed"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post comment on issue {IssueId}", id);
            SetError(localizer["Issue_Comment_PostFailed"].Value);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateIssueStatusModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid issue triage form for {IssueId}", id);
            return BadRequest(ModelState);
        }

        var result = await issues.UpdateStatusAsync(id, viewer, model.Status, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess(localizer["Issue_Status_Updated"].Value);
        }
        else
        {
            SetError(localizer["Issue_Error"].Value);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Assignee")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignee(Guid id, UpdateIssueAssigneeModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid issue triage form for {IssueId}", id);
            return BadRequest(ModelState);
        }

        var result = await issues.UpdateAssigneeAsync(id, viewer, model.AssigneeUserId, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess(localizer["Issue_Assignee_Updated"].Value);
        }
        else
        {
            SetError(localizer["Issue_Error"].Value);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Section")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSection(Guid id, UpdateIssueSectionModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid issue triage form for {IssueId}", id);
            return BadRequest(ModelState);
        }

        var result = await issues.UpdateSectionAsync(id, viewer, model.Section, user.Id);
        if (result.NotFound) return NotFound();
        if (result.Succeeded)
        {
            SetSuccess(localizer["Issue_Section_Updated"].Value);
        }
        else
        {
            SetError(result.ErrorKey is { } key
                ? result.ErrorLimit is { } limit ? localizer[key, limit].Value : localizer[key].Value
                : localizer["Issue_Error"].Value);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetIssueGitHubIssueModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var viewer = ViewerFor(user.Id);
        var issue = await issues.GetIssueByIdAsync(id, viewer);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid issue triage form for {IssueId}", id);
            return BadRequest(ModelState);
        }

        var result = await issues.SetGitHubIssueNumberAsync(id, viewer, model.GitHubIssueNumber, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess(localizer["Issue_GitHub_Linked"].Value);
        }
        else
        {
            SetError(localizer["Issue_Error"].Value);
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private IssueListItemViewModel MapListItem(IssueListSnapshot i) => new()
    {
        Id = i.Id,
        Status = i.Status,
        Category = i.Category,
        AreaLabel = AreaLabelMap.LabelFor(i.Section, localizer),
        Title = i.Title,
        ReporterUserId = i.ReporterUserId,
        LastUpdate = i.UpdatedAt.ToDateTimeUtc(),
        CommentCount = i.CommentCount,
        AssigneeUserId = i.AssigneeUserId,
        GitHubIssueNumber = i.GitHubIssueNumber
    };

    private IssueDetailViewModel MapDetailViewModel(
        IssueDetail i,
        IReadOnlyList<IssueThreadEvent> thread,
        IReadOnlyDictionary<Guid, UserInfo> displayUsers,
        bool isHandler,
        bool isReporter)
    {
        return new IssueDetailViewModel
        {
            Id = i.Id,
            Status = i.Status,
            Category = i.Category,
            Section = i.Section,
            AreaLabel = AreaLabelMap.LabelFor(i.Section, localizer),
            Title = i.Title,
            Description = i.Description,
            PageUrl = i.PageUrl,
            UserAgent = i.UserAgent,
            AdditionalContext = i.AdditionalContext,
            ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
            ReporterUserId = i.ReporterUserId,
            AssigneeUserId = i.AssigneeUserId,
            GitHubIssueNumber = i.GitHubIssueNumber,
            DueDate = i.DueDate,
            CreatedAt = i.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = i.ResolvedByUserId is { } resolvedById
                ? displayUsers.GetValueOrDefault(resolvedById)?.BurnerName
                : null,
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

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> GetIssueDisplayUsersAsync(IssueDetail issue, CancellationToken ct)
    {
        var ids = new HashSet<Guid> { issue.ReporterUserId };
        if (issue.AssigneeUserId is { } assigneeId) ids.Add(assigneeId);
        if (issue.ResolvedByUserId is { } resolvedById) ids.Add(resolvedById);

        return await UserService.GetUserInfosAsync(ids, ct);
    }
}
