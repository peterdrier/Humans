using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Helpers;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Issues")]
public class IssuesController(
    IIssuesService issues,
    IAuthorizationService authorization,
    IUserServiceRead users,
    IUserServiceRead userService,
    IStringLocalizer<SharedResource> localizer,
    ILogger<IssuesController> logger) : HumansControllerBase(userService)
{
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

    // Roles from claims (RoleAssignment → claims-transformation), NOT UserManager.GetRolesAsync (misses CampAdmin etc.).
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

        // Open = non-terminal (matches nav badge); Closed = terminal; Mine = ReporterUserId == current user.
        var statuses = viewMode switch
        {
            IssueViewMode.Open => new[] { IssueStatus.Triage, IssueStatus.Open, IssueStatus.InProgress },
            IssueViewMode.Closed => new[] { IssueStatus.Resolved, IssueStatus.WontFix, IssueStatus.Duplicate },
            _ => null
        };

        // Non-admin: reporter filter forced to self (Mine button). Admin dropdown is independent.
        Guid? reporterFilter = viewMode == IssueViewMode.Mine
            ? user.Id
            : (isAdmin ? reporter : null);

        var filter = new IssueListFilter(
            Statuses: statuses,
            Categories: category.HasValue ? [category.Value] : null,
            Sections: !string.IsNullOrWhiteSpace(section) ? [section] : null,
            ReporterUserId: reporterFilter,
            AssigneeUserId: null,
            SearchText: !string.IsNullOrWhiteSpace(search) ? search : null,
            Limit: 200);

        var issues1 = await issues.GetIssueListAsync(filter, user.Id, roles, isAdmin);

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
            var distinct = await issues.GetDistinctReportersAsync();
            reporterOptions = distinct
                .Select(r => new ReporterDropdownItem
                {
                    UserId = r.UserId,
                    DisplayName = r.DisplayName,
                    Count = r.Count
                })
                .ToList();
        }

        var rows = issues1.Select(MapListItem).ToList();

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

            SetSuccess("Issue filed.");
            return RedirectToAction(nameof(Index), new { selected = issue.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit issue for user {UserId}", user.Id);
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
        var issue = await issues.GetIssueByIdAsync(id);

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

        var thread = await issues.GetThreadAsync(id);
        var displayUsers = await GetIssueDisplayUsersAsync(issue);
        var vm = MapDetailViewModel(issue, thread, displayUsers, isHandler: canHandle, isReporter: isReporter);

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
        var activeIds = (await users.GetAllUserInfosAsync().ConfigureAwait(false))
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToList();
        if (activeIds.Count == 0)
        {
            vm.AssigneeOptions = [];
        }
        else
        {
            var users1 = await users.GetUserInfosAsync(activeIds);
            vm.AssigneeOptions = users1.Values
                .OrderBy(u => u.BurnerName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new AssigneeOption { Id = u.Id, DisplayName = u.BurnerName })
                .ToList();
        }

        // If the current assignee isn't in the active list (left org, etc.),
        // surface them anyway so the dropdown doesn't silently un-assign.
        if (vm.AssigneeUserId.HasValue &&
            vm.AssigneeOptions.All(a => a.Id != vm.AssigneeUserId.Value))
        {
            var inactiveInfo = await users.GetUserInfoAsync(vm.AssigneeUserId.Value);
            vm.AssigneeOptions.Insert(0, new AssigneeOption
            {
                Id = vm.AssigneeUserId.Value,
                DisplayName = (inactiveInfo?.BurnerName ?? "Unknown") + " (inactive)"
            });
        }
    }

    [HttpPost("{id}/Comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostComment(Guid id, PostIssueCommentModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();

        var canHandle = (await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)).Succeeded;
        var isReporter = issue.ReporterUserId == user.Id;
        if (!canHandle && !isReporter) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Comment is required.");
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            await issues.PostCommentAsync(
                id,
                user.Id,
                model.Content,
                senderIsReporter: isReporter,
                resolveOnPost: model.ResolveOnPost && canHandle);

            SetSuccess("Comment posted.");
        }
        catch (InvalidOperationException)
        {
            // Race: issue existed at the null-check above but was deleted
            // before the mutation reached the service.
            logger.LogWarning("Issue {IssueId} not found during PostComment (deleted in race)", id);
            return NotFound();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post comment on issue {IssueId}", id);
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

        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        var result = await issues.UpdateStatusWithResultAsync(id, model.Status, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess("Status updated.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to update status.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Assignee")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignee(Guid id, UpdateIssueAssigneeModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        var result = await issues.UpdateAssigneeWithResultAsync(id, model.AssigneeUserId, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess("Assignee updated.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to update assignee.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Section")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSection(Guid id, UpdateIssueSectionModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        var result = await issues.UpdateSectionWithResultAsync(id, model.Section, user.Id);
        if (result.Succeeded)
        {
            SetSuccess("Section updated.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to update section.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetIssueGitHubIssueModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var issue = await issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var auth = await authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle);
        if (!auth.Succeeded) return Forbid();

        var result = await issues.SetGitHubIssueNumberWithResultAsync(id, model.GitHubIssueNumber, user.Id);
        if (result.NotFound) return NotFound();

        if (result.Succeeded)
        {
            SetSuccess("GitHub issue linked.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to link GitHub issue.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private static IssueListItemViewModel MapListItem(IssueListSnapshot i) => new()
    {
        Id = i.Id,
        Status = i.Status,
        Category = i.Category,
        Section = i.Section,
        AreaLabel = AreaLabelMap.LabelFor(i.Section),
        Title = i.Title,
        ReporterUserId = i.ReporterUserId,
        LastUpdate = i.UpdatedAt.ToDateTimeUtc(),
        CommentCount = i.CommentCount,
        AssigneeUserId = i.AssigneeUserId,
        GitHubIssueNumber = i.GitHubIssueNumber
    };

    private static IssueDetailViewModel MapDetailViewModel(
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
            AreaLabel = AreaLabelMap.LabelFor(i.Section),
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

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> GetIssueDisplayUsersAsync(IssueDetail issue)
    {
        var ids = new HashSet<Guid> { issue.ReporterUserId };
        if (issue.AssigneeUserId is { } assigneeId) ids.Add(assigneeId);
        if (issue.ResolvedByUserId is { } resolvedById) ids.Add(resolvedById);

        return await users.GetUserInfosAsync(ids);
    }
}
