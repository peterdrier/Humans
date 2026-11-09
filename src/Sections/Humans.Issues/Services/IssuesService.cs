using Humans.Auth.Contracts;
using System.Globalization;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;

namespace Humans.Issues.Services;

internal sealed class IssuesService(
    IIssuesRepository repo,
    IUserService users,
    IRoleAssignmentService roles,
    IEmailService email,
    IssuesEmails emailMessages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationInbox,
    IAuditLogService audit,
    IAuditViewerService auditViewer,
    IClock clock,
    IFileStorage fileStorage,
    ISectionCatalog sectionCatalog,
    IssueSectionRouting routing,
    ILogger<IssuesService> logger) : IIssuesService, IUserDataContributor
{
    private readonly IssueReader reader = new(repo, users, auditViewer, fileStorage, routing);

    private readonly IssueDataLifecycle lifecycle = new(repo, clock, fileStorage, logger);

    private readonly IssueNotices notices = new(
        users, roles, email, emailMessages, notifications, notificationInbox, routing, logger);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private const long MaxScreenshotBytes = 10 * 1024 * 1024;
    private const int MaxScreenshotFileNameLength = 256;

    /// <summary>The <c>issues.section</c> column width.</summary>
    private const int MaxSectionLength = 64;

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
        await notices.DispatchSubmittedNotificationAsync(issue, ct);

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

    public Task<IssueDetail?> GetIssueByIdAsync(Guid id, IssueViewer viewer, CancellationToken ct = default) =>
        reader.GetIssueByIdAsync(id, viewer, ct);

    public Task<IssueScreenshot?> GetScreenshotAsync(Guid id, IssueViewer viewer, CancellationToken ct = default) =>
        reader.GetScreenshotAsync(id, viewer, ct);

    public Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter, IssueViewer viewer, CancellationToken ct = default) =>
        reader.GetIssueListAsync(filter, viewer, ct);

    public Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, IssueViewer viewer, CancellationToken ct = default) =>
        reader.GetThreadAsync(issueId, viewer, ct);

    public async Task<IssueCommentResult> PostCommentAsync(
        Guid issueId,
        IssueViewer viewer,
        Guid? senderUserId,
        string content,
        bool resolveOnPost = false,
        CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(issueId, ct);
        if (issue is null || !await reader.CanSeeAsync(issue, viewer, ct))
        {
            logger.LogWarning("Issue {IssueId} not found during PostComment", issueId);
            return new IssueCommentResult(null, NotFound: true);
        }

        // A reporter may comment on their own issue but not resolve it; resolving is a
        // handler's move, so the flag is dropped rather than refused.
        var viewerCanHandle = reader.CanHandle(issue, viewer);

        var senderIsReporter = senderUserId is { } senderId
            && await reader.IsReporterAsync(issue.ReporterUserId, senderId, ct);

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
                IssuesAuditActions.IssueStatusChanged, issueId, senderUserId,
                "reopened (reporter comment on terminal)");
        }

        if (resolveWithComment)
        {
            await LogAuditAsync(IssuesAuditActions.IssueStatusChanged, issueId, senderUserId,
                $"status: {statusBeforeResolution} → {IssueStatus.Resolved}");
        }

        await notices.DispatchCommentNotificationsAsync(issue, comment, senderIsReporter, ct);
        if (resolveWithComment)
        {
            await notices.DispatchStatusChangedNotificationAsync(
                issue, statusBeforeResolution, IssueStatus.Resolved, senderUserId, ct);
            await notices.ResolveSubmittedNotificationsAsync(issue, senderUserId, ct);
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
            IssuesAuditActions.IssueCreated, issue.Id, actorUserId,
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
                IssuesAuditActions.IssueStatusChanged, issueId, actorUserId,
                $"status: {oldStatus} → {newStatus}");
            await notices.DispatchStatusChangedNotificationAsync(issue, oldStatus, newStatus, actorUserId, ct);

            // Nothing left to act on: clear the "New issue filed" alerts.
            if (newStatus.IsTerminal())
                await notices.ResolveSubmittedNotificationsAsync(issue, actorUserId, ct);

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
                IssuesAuditActions.IssueAssigneeChanged, issueId, actorUserId,
                $"assignee: {oldName} → {newName}");

            if (newAssigneeUserId.HasValue)
            {
                await notices.DispatchAssignedNotificationAsync(issue, newAssigneeUserId.Value, actorUserId, ct);
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
                IssuesAuditActions.IssueSectionChanged, issueId, actorUserId,
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
                IssuesAuditActions.IssueGitHubLinked, issueId, actorUserId,
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
            var issue = await reader.FindHandleableAsync(issueId, viewer, ct);
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
        reader.GetActionableCountForViewerAsync(viewer, ct);

    public Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default) =>
        reader.GetDistinctReportersAsync(ct);

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        lifecycle.PurgeExpiredAsync(ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        await lifecycle.ContributeForUserAsync(await reader.UserIdsAsync(userId, ct), ct);

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => IssueDataLifecycle.ErasureDeclaration;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        lifecycle.EraseForUserAsync(userId, ct);

    private async Task LogAuditAsync(
        AuditActionDefinition action, Guid issueId, Guid? actorUserId, string description)
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


}
