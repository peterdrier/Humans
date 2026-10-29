using Humans.Auth.Contracts;
using System.Globalization;
using System.Resources;
using NodaTime;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;

namespace Humans.Issues.Services;

internal sealed class IssuesService(
    IIssuesRepository repo,
    IUserService users,
    IUserEmailService userEmails,
    IRoleAssignmentService roles,
    IEmailService email,
    IssuesEmails emailMessages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationInbox,
    IAuditLogService audit,
    IClock clock,
    IFileStorage fileStorage,
    ISectionCatalog sectionCatalog,
    IssueSectionRouting routing,
    ILogger<IssuesService> logger) : IIssuesService, IUserDataContributor
{
    internal const string Issues = "Issues";
    internal const string IssueComments = "IssueComments";

    private static readonly ResourceManager NoticeResources = new(typeof(IssuesResource));

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private const long MaxScreenshotBytes = 10 * 1024 * 1024;
    private const int MaxScreenshotFileNameLength = 256;

    /// <summary>The <c>issues.section</c> column width.</summary>
    private const int MaxSectionLength = 64;

    /// <summary>Six months after a terminal state, in days so the cutoff is a simple <see cref="Duration"/>.</summary>
    private static readonly Duration RetentionPeriod = Duration.FromDays(180);

    /// <summary>
    /// Both write paths go through this. Checked against the declared queues, not the section catalog: two queue keys
    /// (<c>Profiles</c>, <c>Legal</c>) outlived their section names.
    /// </summary>
    /// <remarks>
    /// An unknown value degrades to null (the Admin queue). Over-length is the one hard failure: nothing legitimate
    /// produces it, and silently truncating would hide a tampered post or a bug.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The value exceeds the column width.</exception>
    private string? NormalizeSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return null;

        var trimmed = section.Trim();

        if (trimmed.Length > MaxSectionLength)
        {
            throw new InvalidOperationException(
                $"Section must be {MaxSectionLength} characters or fewer.");
        }

        var known = routing.Resolve(trimmed);
        if (known is null)
        {
            logger.LogWarning(
                "Issue section {Section} is not a routed queue — routing to the Admin queue. "
                + "Names a discovered section: {IsRealSection}",
                trimmed,
                sectionCatalog.TryResolve(trimmed, out _));
        }

        return known;
    }

    public async Task<IssueSubmissionResult> SubmitIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        string? pageUrl,
        string? userAgent,
        string? additionalContext,
        IFormFile? screenshot,
        LocalDate? dueDate = null,
        IReadOnlyList<string>? reporterRoles = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown issue category.");
        section = NormalizeSection(section);

        var now = clock.GetCurrentInstant();
        var issueId = Guid.NewGuid();
        var storedAdditionalContext = BuildAdditionalContext(additionalContext, reporterRoles);

        var issue = new Issue
        {
            Id = issueId,
            ReporterUserId = reporterUserId,
            Section = section,
            Category = category,
            Title = title,
            Description = description,
            PageUrl = pageUrl,
            UserAgent = userAgent,
            AdditionalContext = storedAdditionalContext,
            Status = IssueStatus.Triage,
            DueDate = dueDate,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (screenshot is { Length: > 0 })
        {
            if (screenshot.Length > MaxScreenshotBytes)
                return new(null, "Issue_ScreenshotTooLarge");

            var contentType = screenshot.ContentType.ToLowerInvariant();
            if (!AllowedContentTypes.Contains(contentType))
                return new(null, "Issue_ScreenshotTypeInvalid");

            var fileName = screenshot.FileName.Split('/', '\\').Last();
            if (fileName.Length > MaxScreenshotFileNameLength)
                return new(null, "Issue_ScreenshotFileNameTooLong");

            var ext = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => throw new InvalidOperationException($"Unexpected content type: {screenshot.ContentType}")
            };

            var key = $"uploads/issues/{issueId}/{Guid.NewGuid()}{ext}";
            await using (var stream = screenshot.OpenReadStream())
            {
                await fileStorage.SaveAsync(key, stream, ct);
            }

            issue.ScreenshotFileName = fileName;
            issue.ScreenshotStoragePath = key;
            issue.ScreenshotContentType = contentType;
        }

        try
        {
            await repo.AddIssueAsync(issue, ct);
        }
        catch when (issue.ScreenshotStoragePath is not null)
        {
            try
            {
                // A failed acknowledgement may follow a committed save; only remove an unreferenced upload.
                if (await repo.GetByIdAsync(issueId, CancellationToken.None) is null)
                    await fileStorage.DeleteAsync(issue.ScreenshotStoragePath, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                logger.LogWarning(cleanupError, "Failed to verify or clean up screenshot after issue {IssueId} submission failed", issueId);
            }
            throw;
        }
        // The issue is committed; finish its notices after a browser disconnect.
        ct = CancellationToken.None;
        await DispatchSubmittedNotificationAsync(issue, ct);

        logger.LogInformation(
            "Issue {IssueId} submitted by {UserId}: {Category}/{Section}",
            issueId, reporterUserId, category, section ?? "(unknown)");
        return new(issue);
    }

    private static string? BuildAdditionalContext(string? userContext, IReadOnlyList<string>? reporterRoles)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userContext))
            parts.Add(userContext);
        if (reporterRoles is { Count: > 0 })
            parts.Add($"roles: {string.Join(", ", reporterRoles.Order(StringComparer.Ordinal))}");

        if (parts.Count == 0)
            return null;

        var result = string.Join(" | ", parts);
        if (result.Length <= 2000) return result;
        var length = char.IsHighSurrogate(result[1999]) && char.IsLowSurrogate(result[2000]) ? 1999 : 2000;
        return result[..length];
    }

    public async Task<IssueDetail?> GetIssueByIdAsync(Guid id, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(id, ct);
        return issue is null || !CanSee(issue, viewer) ? null : MapDetail(issue);
    }

    public async Task<IssueScreenshot?> GetScreenshotAsync(Guid id, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(id, ct);
        if (issue is null || !CanSee(issue, viewer) || issue.ScreenshotStoragePath is not { } key)
            return null;

        var content = await fileStorage.TryReadAsync(key, ct);
        return content is null ? null : new IssueScreenshot(content, issue.ScreenshotContentType ?? "application/octet-stream");
    }

    private bool CanHandle(Issue issue, IssueViewer viewer) =>
        routing.CanHandle(issue.Section, viewer.Roles);

    /// <summary>The same test the queue applies row by row: a handler, or the reporter.</summary>
    private bool CanSee(Issue issue, IssueViewer viewer) =>
        CanHandle(issue, viewer) || issue.ReporterUserId == viewer.UserId;

    /// <summary>
    /// Out of reach and gone are the same answer, so an id cannot probe for issues the caller's queue would never list.
    /// </summary>
    private async Task<Issue?> FindHandleableAsync(Guid issueId, IssueViewer viewer, CancellationToken ct)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct);
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

    public async Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        IssueViewer viewer,
        CancellationToken ct = default)
    {
        IReadOnlySet<string>? sectionFilter = null;
        Guid? reporterFallback = null;

        if (!viewer.IsAdmin)
        {
            sectionFilter = routing.SectionsForRoles(viewer.Roles);
            reporterFallback = viewer.UserId;
        }

        var issues = await repo.GetListAsync(filter, sectionFilter, reporterFallback, ct);
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

    public async Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, IssueViewer viewer, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(issueId, ct);
        if (issue is null || !CanSee(issue, viewer))
            throw new InvalidOperationException($"Issue {issueId} not found");

        // IssueCreated is written only by the machine path, where filer and reporter can differ, so it is the one
        // place the thread can say who actually filed it.
        var auditEntries = await audit.GetFilteredEntriesAsync(
            entityType: AuditEntityTypes.Issue,
            entityId: issueId,
            userId: null,
            actions:
            [
                AuditAction.IssueCreated,
                AuditAction.IssueStatusChanged,
                AuditAction.IssueAssigneeChanged,
                AuditAction.IssueSectionChanged,
                AuditAction.IssueGitHubLinked
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

        var commentEvents = issue.Comments.Select(c => (IssueThreadEvent)new IssueCommentEvent(
            c.Id,
            c.CreatedAt,
            c.SenderUserId,
            c.SenderUserId.HasValue && actorUsers.TryGetValue(c.SenderUserId.Value, out var sender)
                ? sender.BurnerName
                : null,
            ActorIsReporter: c.SenderUserId.HasValue && c.SenderUserId == issue.ReporterUserId,
            c.Content));

        var auditEvents = auditEntries.Select(a => (IssueThreadEvent)new IssueAuditEvent(
            a.OccurredAt,
            a.ActorUserId,
            a.ActorUserId.HasValue && actorUsers.TryGetValue(a.ActorUserId.Value, out var u)
                ? u.BurnerName
                : null,
            a.Action,
            a.Description));

        return commentEvents
            .Concat(auditEvents)
            .OrderBy(e => e.At)
            .ToList();
    }

    public async Task<IssueCommentResult> PostCommentAsync(
        Guid issueId,
        IssueViewer viewer,
        Guid? senderUserId,
        string content,
        bool resolveOnPost = false,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct);
        if (issue is null || !CanSee(issue, viewer))
        {
            logger.LogWarning("Issue {IssueId} not found during PostComment", issueId);
            return new IssueCommentResult(null, NotFound: true);
        }

        // A reporter may comment on their own issue but not resolve it; resolving is a
        // handler's move, so the flag is dropped rather than refused.
        var viewerCanHandle = CanHandle(issue, viewer);

        var senderIsReporter = senderUserId is not null && senderUserId == issue.ReporterUserId;

        var now = clock.GetCurrentInstant();
        var comment = new IssueComment
        {
            Id = Guid.NewGuid(),
            IssueId = issueId,
            SenderUserId = senderUserId,
            Content = content,
            CreatedAt = now
        };

        var statusChangedToOpen = false;
        if (senderIsReporter && issue.Status.IsTerminal())
        {
            SetStatusFields(issue, IssueStatus.Open, senderUserId, now);
            statusChangedToOpen = true;
        }

        var statusBeforeResolution = issue.Status;
        var resolveWithComment = resolveOnPost && viewerCanHandle && !issue.Status.IsTerminal();
        if (resolveWithComment)
            SetStatusFields(issue, IssueStatus.Resolved, senderUserId, now);

        issue.UpdatedAt = now;
        await repo.AddCommentAndSaveIssueAsync(comment, issue, ct);
        // The reply and any status change are committed; finish their follow-up work.
        ct = CancellationToken.None;

        if (statusChangedToOpen)
        {
            await LogAuditAsync(
                AuditAction.IssueStatusChanged, issueId, senderUserId,
                "reopened (reporter comment on terminal)");
        }

        if (resolveWithComment)
        {
            await LogAuditAsync(AuditAction.IssueStatusChanged, issueId, senderUserId,
                $"status: {statusBeforeResolution} → {IssueStatus.Resolved}");
        }

        await DispatchCommentNotificationsAsync(issue, comment, senderIsReporter, ct);
        if (resolveWithComment)
        {
            await DispatchStatusChangedNotificationAsync(
                issue, statusBeforeResolution, IssueStatus.Resolved, senderUserId, ct);
            await ResolveSubmittedNotificationsAsync(issue, senderUserId, ct);
        }

        logger.LogInformation(
            "Comment posted on issue {IssueId} by {UserId} (reporter: {Reporter})",
            issueId, senderUserId, senderIsReporter);

        return new IssueCommentResult(new IssueCommentInfo(comment.Id, comment.Content, comment.CreatedAt));
    }

    public async Task<Guid> CreateIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        LocalDate? dueDate = null,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        var submission = await SubmitIssueAsync(
            reporterUserId, category, title, description, section,
            pageUrl: null, userAgent: null, additionalContext: null, screenshot: null,
            dueDate: dueDate, ct: ct);
        var issue = submission.Issue!;

        // In-app the reporter is their own actor, so creation goes unaudited. Here the two can differ, and the audit
        // entry is the only place the filer is written down.
        await LogAuditAsync(
            AuditAction.IssueCreated, issue.Id, actorUserId,
            actorUserId == reporterUserId
                ? "Issue filed"
                : $"Issue filed on behalf of {reporterUserId}");

        return issue.Id;
    }

    private static void SetStatusFields(Issue issue, IssueStatus newStatus, Guid? actorUserId, Instant now)
    {
        var oldStatus = issue.Status;
        issue.Status = newStatus;
        issue.UpdatedAt = now;
        if (newStatus.IsTerminal())
        {
            issue.ResolvedAt = now;
            issue.ResolvedByUserId = actorUserId;
        }
        else if (oldStatus.IsTerminal())
        {
            issue.ResolvedAt = null;
            issue.ResolvedByUserId = null;
        }
    }

    public Task<IssueMutationResult> UpdateStatusAsync(
        Guid issueId, IssueViewer viewer, IssueStatus newStatus, Guid? actorUserId,
        CancellationToken ct = default)
    {
        return MutateAsync(issueId, viewer, "UpdateStatus", "Failed to update status.", async issue =>
        {
            if (!Enum.IsDefined(newStatus))
            {
                logger.LogWarning("Issue {IssueId} status update rejected: undefined status {Status}", issueId, newStatus);
                return IssueMutationResult.Refused("Unknown issue status.");
            }

            var oldStatus = issue.Status;
            if (oldStatus == newStatus) return IssueMutationResult.Success();

            SetStatusFields(issue, newStatus, actorUserId, clock.GetCurrentInstant());

            await repo.SaveTrackedIssueAsync(issue, ct);
            // The mutation is committed; finish its notices independently of the request.
            ct = CancellationToken.None;
            await LogAuditAsync(
                AuditAction.IssueStatusChanged, issueId, actorUserId,
                $"status: {oldStatus} → {newStatus}");
            await DispatchStatusChangedNotificationAsync(issue, oldStatus, newStatus, actorUserId, ct);

            // Nothing left to act on: clear the "New issue filed" alerts.
            if (newStatus.IsTerminal())
                await ResolveSubmittedNotificationsAsync(issue, actorUserId, ct);

            return IssueMutationResult.Success();
        }, ct);
    }

    public Task<IssueMutationResult> UpdateAssigneeAsync(
        Guid issueId, IssueViewer viewer, Guid? newAssigneeUserId, Guid? actorUserId,
        CancellationToken ct = default)
    {
        return MutateAsync(issueId, viewer, "UpdateAssignee", "Failed to update assignee.", async issue =>
        {
            if (issue.AssigneeUserId == newAssigneeUserId) return IssueMutationResult.Success();

            var oldAssigneeId = issue.AssigneeUserId;
            var idsToResolve = new List<Guid>(2);
            if (oldAssigneeId.HasValue) idsToResolve.Add(oldAssigneeId.Value);
            if (newAssigneeUserId.HasValue) idsToResolve.Add(newAssigneeUserId.Value);

            var users1 = idsToResolve.Count == 0
                ? null
                : await users.GetUserInfosAsync(idsToResolve, ct);

            var oldName = oldAssigneeId.HasValue
                ? (users1!.TryGetValue(oldAssigneeId.Value, out var ou) ? ou.BurnerName : oldAssigneeId.Value.ToString())
                : "Unassigned";
            var newName = newAssigneeUserId.HasValue
                ? (users1!.TryGetValue(newAssigneeUserId.Value, out var nu) ? nu.BurnerName : newAssigneeUserId.Value.ToString())
                : "Unassigned";

            // Only an active member may be assigned — the same set the assignee picker offers.
            // Store the resolved id: a merged-away assignee id would point the issue at a tombstone.
            if (newAssigneeUserId is { } requested)
            {
                if (!users1!.TryGetValue(requested, out var resolved) || !resolved.IsActive)
                {
                    logger.LogWarning("Issue {IssueId} assignee update rejected: {UserId} is not an active member", issueId, requested);
                    return IssueMutationResult.Refused("Only an active member can be assigned.");
                }
                newAssigneeUserId = resolved.Id;
            }
            if (issue.AssigneeUserId == newAssigneeUserId) return IssueMutationResult.Success();

            issue.AssigneeUserId = newAssigneeUserId;
            issue.UpdatedAt = clock.GetCurrentInstant();
            await repo.SaveTrackedIssueAsync(issue, ct);
            // The mutation is committed; finish its notices independently of the request.
            ct = CancellationToken.None;

            await LogAuditAsync(
                AuditAction.IssueAssigneeChanged, issueId, actorUserId,
                $"assignee: {oldName} → {newName}");

            if (newAssigneeUserId.HasValue)
            {
                await DispatchAssignedNotificationAsync(issue, newAssigneeUserId.Value, actorUserId, ct);
            }

            return IssueMutationResult.Success();
        }, ct);
    }

    public Task<IssueMutationResult> UpdateSectionAsync(
        Guid issueId, IssueViewer viewer, string? newSection, Guid? actorUserId,
        CancellationToken ct = default)
    {
        // Authorized against the section the issue is in, not the one it is going to — the
        // same test the browser applies, where a handler routes an issue out of their queue.
        return MutateAsync(issueId, viewer, "UpdateSection", "Failed to update section.", async issue =>
        {
            if (newSection?.Trim().Length > MaxSectionLength)
            {
                logger.LogWarning("Issue {IssueId} section update rejected: section exceeds {MaxLength} characters", issueId, MaxSectionLength);
                return IssueMutationResult.Refused($"Section must be {MaxSectionLength} characters or fewer.",
                    "Issue_Section_TooLong", MaxSectionLength);
            }
            newSection = NormalizeSection(newSection);

            if (string.Equals(issue.Section, newSection, StringComparison.Ordinal)) return IssueMutationResult.Success();

            if (issue.Status.IsTerminal())
            {
                var reason = $"Cannot change section on a terminal issue (status: {issue.Status}).";
                logger.LogWarning("Issue {IssueId} UpdateSection rejected: {Reason}", issueId, reason);
                return IssueMutationResult.Refused(reason, "Issue_Section_Terminal");
            }

            var previousSection = issue.Section;
            var oldSection = previousSection ?? "(unknown)";
            var nextSection = newSection ?? "(unknown)";

            issue.Section = newSection;
            issue.UpdatedAt = clock.GetCurrentInstant();
            await repo.SaveTrackedIssueAsync(issue, ct);

            await LogAuditAsync(
                AuditAction.IssueSectionChanged, issueId, actorUserId,
                $"section: {oldSection} → {nextSection}");
            return IssueMutationResult.Success();
        }, ct);
    }

    public Task<IssueMutationResult> SetGitHubIssueNumberAsync(
        Guid issueId, IssueViewer viewer, int? githubIssueNumber, Guid? actorUserId,
        CancellationToken ct = default)
    {
        return MutateAsync(issueId, viewer, "SetGitHubIssueNumber", "Failed to link GitHub issue.", async issue =>
        {
            if (githubIssueNumber is <= 0)
            {
                logger.LogWarning("Issue {IssueId} rejected nonpositive GitHub issue number {IssueNumber}", issueId, githubIssueNumber);
                return IssueMutationResult.Refused("GitHub issue numbers must be positive.");
            }

            if (issue.GitHubIssueNumber == githubIssueNumber) return IssueMutationResult.Success();

            issue.GitHubIssueNumber = githubIssueNumber;
            issue.UpdatedAt = clock.GetCurrentInstant();
            await repo.SaveTrackedIssueAsync(issue, ct);

            await LogAuditAsync(
                AuditAction.IssueGitHubLinked, issueId, actorUserId,
                $"GitHub link: {(githubIssueNumber.HasValue ? githubIssueNumber.Value.ToString(CultureInfo.InvariantCulture) : "(cleared)")}");

            return IssueMutationResult.Success();
        }, ct);
    }

    /// <summary>Out of reach and gone both answer Missing.</summary>
    private async Task<IssueMutationResult> MutateAsync(
        Guid issueId, IssueViewer viewer, string operation, string failure,
        Func<Issue, Task<IssueMutationResult>> apply, CancellationToken ct)
    {
        try
        {
            var issue = await FindHandleableAsync(issueId, viewer, ct);
            if (issue is null)
            {
                logger.LogWarning("Issue {IssueId} not found during {Operation:l}", issueId, operation);
                return IssueMutationResult.Missing("Issue not found.");
            }

            return await apply(issue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to apply {Operation:l} on issue {IssueId}", operation, issueId);
            return IssueMutationResult.Failed(failure);
        }
    }

    public Task<int> GetActionableCountForViewerAsync(IssueViewer viewer, CancellationToken ct = default) =>
        viewer.IsAdmin
            ? repo.CountActionableAsync(null, null, ct)
            : repo.CountActionableAsync(routing.SectionsForRoles(viewer.Roles), viewer.UserId, ct);

    public async Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(
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

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - RetentionPeriod;

        var expired = await repo.GetExpiredTerminalAsync(cutoff, ct);
        if (expired.Count == 0) return 0;

        var (deleted, screenshotKeys) = await repo.DeleteByIdsAsync(expired, ct);

        await DeleteScreenshotsAsync(screenshotKeys);

        logger.LogInformation(
            "PurgeExpiredAsync: deleted {Count} issues older than {Cutoff} (retention {Days}d)",
            deleted, cutoff, RetentionPeriod.Days);

        return deleted;
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        var userIds = (user?.AllUserIds ?? [userId]).ToHashSet();
        var issues = new List<Issue>();
        foreach (var id in userIds)
            issues.AddRange(await repo.GetForUserExportAsync(id, ct));
        var distinctIssues = issues.DistinctBy(i => i.Id).ToList();
        var shaped = distinctIssues.Where(i => userIds.Contains(i.ReporterUserId)).Select(i => new
        {
            i.Title,
            i.Description,
            i.Category,
            i.Section,
            i.Status,
            i.PageUrl,
            i.UserAgent,
            i.AdditionalContext,
            CreatedAt = i.CreatedAt.ToIso8601(),
            ResolvedAt = i.ResolvedAt.ToIso8601(),
            Comments = i.Comments.OrderBy(c => c.CreatedAt).Select(c => new
            {
                c.Content,
                IsFromUser = c.SenderUserId is { } senderId && userIds.Contains(senderId),
                CreatedAt = c.CreatedAt.ToIso8601()
            })
        }).ToList();

        var comments = distinctIssues
            .Where(i => !userIds.Contains(i.ReporterUserId))
            .SelectMany(i => i.Comments)
            .Where(c => c.SenderUserId is { } senderId && userIds.Contains(senderId))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.IssueId,
                c.Content,
                CreatedAt = c.CreatedAt.ToIso8601()
            }).ToList();

        return [new UserDataSlice(Issues, shaped), new UserDataSlice(IssueComments, comments)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Issues] = null, // Own issues, comments and screenshots are deleted outright.
            [IssueComments] =
                "Partially retained: comments on other people's issues remain as that issue's content, " +
                "with authorship detached (SenderUserId nulled), per GDPR Art. 17(3)(b)."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Reported issues and their free text are hard-deleted; triage links elsewhere are detached.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var ownIssues = await repo.GetForUserExportAsync(userId, ct);
        var screenshotKeys = ownIssues.Where(i => i.ReporterUserId == userId)
            .Select(i => i.ScreenshotStoragePath).OfType<string>().Distinct(StringComparer.Ordinal);
        ct = CancellationToken.None;
        foreach (var key in screenshotKeys)
            await fileStorage.DeleteAsync(key, ct);

        await repo.EraseForUserAsync(userId, ct);
    }

    /// <summary>
    /// Best-effort screenshot cleanup after retention has committed its row removal.
    /// </summary>
    private async Task DeleteScreenshotsAsync(IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await fileStorage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete issue screenshot {Key}", key);
            }
        }
    }

    private async Task LogAuditAsync(
        AuditAction action, Guid issueId, Guid? actorUserId, string description)
    {
        if (actorUserId.HasValue)
        {
            await audit.LogAsync(action, AuditEntityTypes.Issue, issueId, description, actorUserId.Value);
        }
        else
        {
            await audit.LogAsync(action, AuditEntityTypes.Issue, issueId, description, "API");
        }
    }

    private async Task DispatchCommentNotificationsAsync(
        Issue issue, IssueComment comment, bool senderIsReporter, CancellationToken ct)
    {
        var link = $"/Issues/{issue.Id}";
        var recipients = new HashSet<Guid>();

        if (senderIsReporter)
        {
            recipients.UnionWith(await QueueHandlerIdsAsync(issue.Section, ct));
            if (comment.SenderUserId is { } senderId) recipients.Remove(senderId);
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);
        }
        else
        {
            if (issue.ReporterUserId != comment.SenderUserId)
                recipients.Add(issue.ReporterUserId);
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);

            await SendCommentEmailAsync(issue, comment, ct);
        }

        var targets = await ResolveRecipientsAsync(recipients, comment.SenderUserId, ct);
        if (targets.Count == 0) return;

        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueComment,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                targets,
                culture => (Notice(culture, "Issue_Notice_CommentTitle", issue.Title), comment.Content),
                link, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueComment notifications for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task SendCommentEmailAsync(Issue issue, IssueComment comment, CancellationToken ct)
    {
        try
        {
            var reporter = await users.GetUserInfoAsync(issue.ReporterUserId, ct);
            var emails = await userEmails.GetNotificationTargetEmailsAsync(
                [issue.ReporterUserId], ct);

            if (reporter is not null &&
                emails.TryGetValue(issue.ReporterUserId, out var to) &&
                !string.IsNullOrWhiteSpace(to))
            {
                var language = reporter.PreferredLanguage;
                await email.SendAsync(emailMessages.IssueComment(
                    reporter.Id,
                    to,
                    reporter.BurnerName,
                    issue.Title,
                    comment.Content,
                    $"/Issues/{issue.Id}",
                    language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode),
                    ct);
            }
            else
            {
                logger.LogWarning(
                    "Skipping issue comment email for issue {IssueId} — reporter {UserId} has no effective email",
                    issue.Id, issue.ReporterUserId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Issue comment {CommentId} saved but its reporter email failed for issue {IssueId}",
                comment.Id, issue.Id);
        }
    }

    private async Task DispatchStatusChangedNotificationAsync(
        Issue issue, IssueStatus oldStatus, IssueStatus newStatus,
        Guid? actorUserId, CancellationToken ct)
    {
        // Both must differ from the actor.
        var recipients = new HashSet<Guid>();
        if (issue.ReporterUserId != actorUserId) recipients.Add(issue.ReporterUserId);
        if (issue.AssigneeUserId is { } aid && aid != actorUserId) recipients.Add(aid);
        var targets = await ResolveRecipientsAsync(recipients, actorUserId, ct);
        if (targets.Count == 0) return;

        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueStatusChanged,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                targets,
                culture => (
                    Notice(culture, "Issue_Notice_StatusChangedTitle", issue.Title),
                    Notice(culture, "Issue_Notice_StatusChangedBody",
                        Notice(culture, $"Enum_IssueStatus_{oldStatus}"),
                        Notice(culture, $"Enum_IssueStatus_{newStatus}"))),
                $"/Issues/{issue.Id}", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueStatusChanged notification for issue {IssueId}",
                issue.Id);
        }
    }

    /// <summary>Admins when no role owns the queue.</summary>
    private async Task<HashSet<Guid>> QueueHandlerIdsAsync(string? section, CancellationToken ct)
    {
        var queueRoles = routing.RolesFor(section);
        IEnumerable<string> notified = queueRoles.Count > 0 ? queueRoles : [RoleNames.Admin];
        var ids = new HashSet<Guid>();
        foreach (var role in notified)
            ids.UnionWith(await roles.GetActiveUserIdsInRoleAsync(role, ct));
        return ids;
    }

    private async Task DispatchSubmittedNotificationAsync(Issue issue, CancellationToken ct)
    {
        var recipients = await QueueHandlerIdsAsync(issue.Section, ct);
        recipients.Remove(issue.ReporterUserId);

        if (recipients.Count == 0) return;

        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueSubmitted,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                recipients.ToList(),
                culture => (Notice(culture, "Issue_Notice_SubmittedTitle", issue.Title), issue.Description),
                $"/Issues/{issue.Id}", ct, issue.Id.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueSubmitted notification for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task ResolveSubmittedNotificationsAsync(
        Issue issue, Guid? actorUserId, CancellationToken ct)
    {
        try
        {
            await notificationInbox.ResolveBySourceKeyAsync(
                IssuesNotificationSources.IssueSubmitted, issue.Id.ToString(), actorUserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resolve IssueSubmitted notifications for issue {IssueId}",
                issue.Id);
        }
    }

    /// <summary>
    /// A stored reporter or assignee id since merged away is delivered to its survivor, and self-exclusion is judged on
    /// the resolved id.
    /// </summary>
    private async Task<List<Guid>> ResolveRecipientsAsync(
        IEnumerable<Guid> userIds, Guid? exclude, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var infos = await users.GetUserInfosAsync(ids, ct);
        return ids
            .Select(id => infos.TryGetValue(id, out var info) ? info.Id : id)
            .Where(id => id != exclude)
            .Distinct()
            .ToList();
    }

    private async Task DispatchAssignedNotificationAsync(
        Issue issue, Guid newAssigneeUserId, Guid? actorUserId, CancellationToken ct)
    {
        if (newAssigneeUserId == actorUserId) return;

        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueAssigned,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                [newAssigneeUserId],
                culture => (Notice(culture, "Issue_Notice_AssignedTitle", issue.Title), issue.Description),
                $"/Issues/{issue.Id}", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueAssigned notification for issue {IssueId}",
                issue.Id);
        }
    }

    private static string Notice(CultureInfo culture, string key, params object[] arguments) =>
        string.Format(culture, NoticeResources.GetString(key, culture)!, arguments);

    private async Task SendLocalizedNotificationAsync(
        NotificationSourceDefinition source,
        NotificationClass notificationClass,
        NotificationPriority priority,
        IReadOnlyList<Guid> recipients,
        Func<CultureInfo, (string Title, string? Body)> content,
        string actionUrl,
        CancellationToken ct,
        string? sourceKey = null)
    {
        var people = await users.GetUserInfosAsync(recipients, ct);
        foreach (var group in recipients.GroupBy(
                     id =>
                     {
                         var language = people.GetValueOrDefault(id)?.PreferredLanguage;
                         return language.IsSupportedCultureCode() ? language! : "en";
                     },
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(group.Key);
                var (title, body) = content(culture);
                await notifications.SendAsync(
                    source, notificationClass, priority, title, group.ToList(), body,
                    actionUrl: actionUrl,
                    actionLabel: Notice(culture, "Issue_Notice_Open"),
                    sourceKey: sourceKey,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to dispatch {Source} issue notices for language {Language}", source, group.Key);
            }
        }
    }
}
