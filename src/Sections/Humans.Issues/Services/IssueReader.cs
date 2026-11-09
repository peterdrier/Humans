using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using Humans.Users.Contracts;

namespace Humans.Issues.Services;

internal sealed class IssueReader(
    IIssuesRepository repo,
    IUserService users,
    IAuditViewerService auditViewer,
    IFileStorage fileStorage,
    IssueSectionRouting routing) : IApplicationService
{
    internal async Task<IssueDetail?> GetIssueByIdAsync(Guid id, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(id, ct);
        return issue is null || !await CanSeeAsync(issue, viewer, ct) ? null : MapDetail(issue);
    }

    internal async Task<IssueScreenshot?> GetScreenshotAsync(Guid id, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(id, ct);
        if (issue is null || !await CanSeeAsync(issue, viewer, ct) || issue.ScreenshotStoragePath is not { } key)
            return null;

        var content = await fileStorage.TryReadAsync(key, ct);
        return content is null ? null : new IssueScreenshot(content, issue.ScreenshotContentType ?? "application/octet-stream");
    }

    internal bool CanHandle(Issue issue, IssueViewer viewer) =>
        routing.CanHandle(issue.Section, viewer.Roles);

    /// <summary>The same test the queue applies row by row: a handler, or the reporter.</summary>
    internal async Task<bool> CanSeeAsync(Issue issue, IssueViewer viewer, CancellationToken ct) =>
        CanHandle(issue, viewer) || await IsReporterAsync(issue.ReporterUserId, viewer.UserId, ct);

    internal async Task<IReadOnlyList<Guid>> UserIdsAsync(Guid userId, CancellationToken ct) =>
        (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    internal async Task<bool> IsReporterAsync(Guid reporterId, Guid userId, CancellationToken ct) =>
        reporterId == userId || (await UserIdsAsync(userId, ct)).Contains(reporterId);

    /// <summary>
    /// Out of reach and gone are the same answer, so an id cannot probe for issues the caller's queue would never list.
    /// </summary>
    internal async Task<Issue?> FindHandleableAsync(Guid issueId, IssueViewer viewer, CancellationToken ct)
    {
        var issue = await repo.GetByIdAsync(issueId, ct);
        return issue is not null && CanHandle(issue, viewer)
            ? issue
            : null;
    }

    private static IssueDetail MapDetail(Issue issue) => new(
        issue.Id,
        issue.Status,
        issue.Category,
        issue.Section,
        issue.Title,
        issue.Description,
        issue.PageUrl,
        issue.UserAgent,
        issue.AdditionalContext,
        issue.ScreenshotStoragePath,
        issue.ReporterUserId,
        issue.AssigneeUserId,
        issue.ResolvedByUserId,
        issue.GitHubIssueNumber,
        issue.DueDate,
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.ResolvedAt,
        issue.Comments.Count);

    internal async Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        IssueViewer viewer,
        CancellationToken ct = default)
    {
        IReadOnlySet<string>? sectionFilter = null;
        IReadOnlyCollection<Guid>? reporterFallback = null;

        if (!viewer.IsAdmin)
        {
            sectionFilter = routing.SectionsForRoles(viewer.Roles);
            reporterFallback = await UserIdsAsync(viewer.UserId, ct);
        }

        var reporterFilter = filter.ReporterUserId is { } reporterId
            ? await UserIdsAsync(reporterId, ct) : null;
        var assigneeFilter = filter.AssigneeUserId is { } assigneeId
            ? await UserIdsAsync(assigneeId, ct) : null;
        var issues = await repo.GetListAsync(filter, sectionFilter, reporterFallback, reporterFilter, assigneeFilter, ct);
        return await BuildListSnapshotsAsync(issues, ct);
    }

    private async Task<IReadOnlyList<IssueListSnapshot>> BuildListSnapshotsAsync(
        IReadOnlyList<Issue> issues, CancellationToken ct)
    {
        if (issues.Count == 0) return [];

        var userIds = new HashSet<Guid>();
        foreach (var issue in issues)
        {
            userIds.Add(issue.ReporterUserId);
            if (issue.AssigneeUserId.HasValue)
                userIds.Add(issue.AssigneeUserId.Value);
        }

        var users1 = await users.GetUserInfosAsync(userIds.ToList(), ct);
        return issues.Select(issue => new IssueListSnapshot(
            issue.Id,
            issue.Status,
            issue.Category,
            issue.Section,
            issue.Title,
            issue.Description,
            issue.PageUrl,
            issue.UserAgent,
            issue.AdditionalContext,
            issue.ReporterUserId,
            users1.TryGetValue(issue.ReporterUserId, out var reporter) ? reporter.BurnerName : null,
            reporter?.Email,
            reporter?.PreferredLanguage,
            issue.CreatedAt,
            issue.UpdatedAt,
            issue.ResolvedAt,
            issue.DueDate,
            issue.ScreenshotStoragePath,
            issue.Comments.Count,
            issue.AssigneeUserId,
            issue.AssigneeUserId.HasValue && users1.TryGetValue(issue.AssigneeUserId.Value, out var assignee)
                ? assignee.BurnerName
                : null,
            issue.GitHubIssueNumber)).ToList();
    }

    internal async Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(issueId, ct);
        if (issue is null || !await CanSeeAsync(issue, viewer, ct))
            throw new InvalidOperationException($"Issue {issueId} not found");

        // IssueCreated is written only by the machine path, where filer and reporter can differ, so it is the one
        // place the thread can say who actually filed it.
        var auditEntries = await auditViewer.GetFilteredAsync(
            entityType: AuditEntityTypes.Issue,
            entityId: issueId,
            userId: null,
            actions:
            [
                IssuesAuditActions.IssueCreated.Name,
                IssuesAuditActions.IssueStatusChanged.Name,
                IssuesAuditActions.IssueAssigneeChanged.Name,
                IssuesAuditActions.IssueSectionChanged.Name,
                IssuesAuditActions.IssueGitHubLinked.Name
            ],
            limit: int.MaxValue,
            ct: ct);

        var actorIds = auditEntries
            .Where(a => a.ActorUserId.HasValue)
            .Select(a => a.ActorUserId!.Value)
            .Concat(issue.Comments.Where(c => c.SenderUserId.HasValue).Select(c => c.SenderUserId!.Value))
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, UserInfo> actorUsers = actorIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await users.GetUserInfosAsync(actorIds, ct);

        var reporterIds = await UserIdsAsync(issue.ReporterUserId, ct);
        var commentEvents = issue.Comments.Select(c => (IssueThreadEvent)new IssueCommentEvent(
            c.Id,
            c.CreatedAt,
            c.SenderUserId,
            c.SenderUserId.HasValue && actorUsers.TryGetValue(c.SenderUserId.Value, out var sender)
                ? sender.BurnerName
                : null,
            ActorIsReporter: c.SenderUserId is { } senderId && reporterIds.Contains(senderId),
            c.Content));

        var auditEvents = auditEntries.Select(a => (IssueThreadEvent)new IssueAuditEvent(
            a.OccurredAt,
            a.ActorUserId,
            a.ActorUserId.HasValue && actorUsers.TryGetValue(a.ActorUserId.Value, out var u)
                ? u.BurnerName
                : null,
            a.Action.Name,
            a.Description));

        return commentEvents
            .Concat(auditEvents)
            .OrderBy(e => e.At)
            .ToList();
    }

    internal async Task<int> GetActionableCountForViewerAsync(IssueViewer viewer, CancellationToken ct = default) =>
        viewer.IsAdmin
            ? await repo.CountActionableAsync(null, null, ct)
            : await repo.CountActionableAsync(routing.SectionsForRoles(viewer.Roles), await UserIdsAsync(viewer.UserId, ct), ct);

    internal async Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(
        CancellationToken ct = default)
    {
        var rows = await repo.GetReporterCountsAsync(ct);
        if (rows.Count == 0) return [];

        var users1 = await users.GetUserInfosAsync(rows.Select(r => r.UserId).ToList(), ct);
        return rows
            .Select(r => new DistinctReporterRow(
                r.UserId,
                users1.TryGetValue(r.UserId, out var u) ? u.BurnerName : r.UserId.ToString(),
                r.Count))
            .ToList();
    }

}
