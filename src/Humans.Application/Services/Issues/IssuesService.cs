using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Issues;

/// <summary>
/// <see cref="IIssuesService"/> impl. Data via <see cref="IIssuesRepository"/>; cross-section reads via user/email/role services;
/// audit after save (design-rules §7a); notifications and emails dispatched at the end.
/// </summary>
public sealed class IssuesService(
    IIssuesRepository repo,
    IUserService users,
    IUserEmailService userEmails,
    IRoleAssignmentService roles,
    IEmailService email,
    INotificationService notifications,
    IAuditLogService audit,
    INavBadgeCacheInvalidator navBadge,
    IIssuesBadgeCacheInvalidator issuesBadge,
    IMemoryCache cache,
    IClock clock,
    IHostEnvironment env,
    ILogger<IssuesService> logger) : IIssuesService, IUserDataContributor
{
    private static readonly TimeSpan BadgeCacheDuration = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private const long MaxScreenshotBytes = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// Issues are kept for 6 months after they enter a terminal state. After
    /// that the row + comments + screenshot directory are removed by
    /// <c>CleanupIssuesJob</c>. Days, not months, so the cutoff is a simple
    /// <see cref="Duration"/>.
    /// </summary>
    private static readonly Duration RetentionPeriod = Duration.FromDays(180);

    // ─── Submission ───

    public async Task<Issue> SubmitIssueAsync(
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
        CancellationToken ct = default) =>
        await SubmitIssueAsync(
            reporterUserId,
            category,
            title,
            description,
            section,
            pageUrl,
            userAgent,
            additionalContext,
            screenshot,
            dueDate,
            reporterRoles: null,
            ct);

    public async Task<Issue> SubmitIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        string? pageUrl,
        string? userAgent,
        string? additionalContext,
        IFormFile? screenshot,
        LocalDate? dueDate,
        IReadOnlyList<string>? reporterRoles,
        CancellationToken ct = default)
    {
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
                throw new InvalidOperationException("Screenshot must be under 10MB.");

            if (!AllowedContentTypes.Contains(screenshot.ContentType))
                throw new InvalidOperationException("Screenshot must be JPEG, PNG, or WebP.");

            var ext = screenshot.ContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => throw new InvalidOperationException($"Unexpected content type: {screenshot.ContentType}")
            };

            var fileName = $"{Guid.NewGuid()}{ext}";
            var relativePath = Path.Combine("uploads", "issues", issueId.ToString(), fileName);
            var absolutePath = Path.Combine(env.ContentRootPath, "wwwroot", relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            await using var stream = new FileStream(absolutePath, FileMode.Create);
            await screenshot.CopyToAsync(stream, ct);

            issue.ScreenshotFileName = screenshot.FileName;
            issue.ScreenshotStoragePath = relativePath.Replace('\\', '/');
            issue.ScreenshotContentType = screenshot.ContentType;
        }

        await repo.AddIssueAsync(issue, ct);
        navBadge.Invalidate();
        issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(reporterUserId, section, null, ct));

        await DispatchSubmittedNotificationAsync(issue, ct);

        logger.LogInformation(
            "Issue {IssueId} submitted by {UserId}: {Category}/{Section}",
            issueId, reporterUserId, category, section ?? "(unknown)");
        return issue;
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
        return result.Length > 2000 ? result[..2000] : result;
    }

    // ─── Reads ───

    public async Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default)
    {
        var issue = await repo.GetByIdAsync(id, ct);
        if (issue is null) return null;
        await StitchCrossDomainNavsAsync([issue], ct);
        return issue;
    }

    public async Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default)
    {
        IReadOnlySet<string>? sectionFilter = null;
        Guid? reporterFallback = null;

        if (!viewerIsAdmin)
        {
            sectionFilter = IssueSectionRouting.SectionsForRoles(viewerRoles);
            reporterFallback = viewerUserId;
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
        Guid issueId, CancellationToken ct = default)
    {
        var issue = await GetIssueByIdAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        // Audit entries for the four Issue-related actions.
        var auditEntries = await audit.GetFilteredEntriesAsync(
            entityType: nameof(Issue),
            entityId: issueId,
            userId: null,
            actions:
            [
                AuditAction.IssueStatusChanged,
                AuditAction.IssueAssigneeChanged,
                AuditAction.IssueSectionChanged,
                AuditAction.IssueGitHubLinked
            ],
            limit: int.MaxValue,
            ct: ct);

        // Resolve actor display names (audit log DTO doesn't include them).
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

    // ─── Mutations ───

    public async Task<IssueComment> PostCommentAsync(
        Guid issueId,
        Guid? senderUserId,
        string content,
        bool senderIsReporter,
        CancellationToken ct = default) =>
        await PostCommentAsync(issueId, senderUserId, content, senderIsReporter, resolveOnPost: false, ct);

    public async Task<IssueComment> PostCommentAsync(
        Guid issueId,
        Guid? senderUserId,
        string content,
        bool senderIsReporter,
        bool resolveOnPost,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        var now = clock.GetCurrentInstant();
        var comment = new IssueComment
        {
            Id = Guid.NewGuid(),
            IssueId = issueId,
            SenderUserId = senderUserId,
            Content = content,
            CreatedAt = now
        };

        // Reporter posting on a terminal issue auto-reopens to Open and clears
        // the resolved fields.
        var statusChangedToOpen = false;
        if (senderIsReporter && issue.Status.IsTerminal())
        {
            issue.Status = IssueStatus.Open;
            issue.ResolvedAt = null;
            issue.ResolvedByUserId = null;
            statusChangedToOpen = true;
        }

        issue.UpdatedAt = now;
        await repo.AddCommentAndSaveIssueAsync(comment, issue, ct);

        if (statusChangedToOpen)
        {
            await LogAuditAsync(
                AuditAction.IssueStatusChanged, issueId, senderUserId,
                "reopened (reporter comment on terminal)");
        }

        await DispatchCommentNotificationsAsync(issue, comment, senderIsReporter, ct);
        navBadge.Invalidate();
        if (statusChangedToOpen)
        {
            issuesBadge.InvalidateMany(
                await ResolveBadgeUserIdsAsync(issue.ReporterUserId, issue.Section, null, ct));
        }

        logger.LogInformation(
            "Comment posted on issue {IssueId} by {UserId} (reporter: {Reporter})",
            issueId, senderUserId, senderIsReporter);

        if (resolveOnPost && !issue.Status.IsTerminal())
        {
            await UpdateStatusAsync(issueId, IssueStatus.Resolved, senderUserId, ct);
        }

        return comment;
    }

    public async Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        var oldStatus = issue.Status;
        if (oldStatus == newStatus) return;

        var now = clock.GetCurrentInstant();
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

        await repo.SaveTrackedIssueAsync(issue, ct);
        await LogAuditAsync(
            AuditAction.IssueStatusChanged, issueId, actorUserId,
            $"status: {oldStatus} → {newStatus}");
        await DispatchStatusChangedNotificationAsync(issue, oldStatus, newStatus, actorUserId, ct);
        navBadge.Invalidate();
        issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(issue.ReporterUserId, issue.Section, null, ct));
    }

    public async Task<IssueMutationResult> UpdateStatusWithResultAsync(
        Guid issueId,
        IssueStatus newStatus,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateStatusAsync(issueId, newStatus, actorUserId, ct);
            return IssueMutationResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Issue {IssueId} not found during UpdateStatus", issueId);
            return IssueMutationResult.Missing("Issue not found.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update issue {IssueId} status", issueId);
            return IssueMutationResult.Failed("Failed to update status.");
        }
    }

    public async Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        if (issue.AssigneeUserId == newAssigneeUserId) return;

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

        issue.AssigneeUserId = newAssigneeUserId;
        issue.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveTrackedIssueAsync(issue, ct);

        await LogAuditAsync(
            AuditAction.IssueAssigneeChanged, issueId, actorUserId,
            $"assignee: {oldName} → {newName}");

        if (newAssigneeUserId.HasValue)
        {
            await DispatchAssignedNotificationAsync(issue, newAssigneeUserId.Value, actorUserId, ct);
        }
    }

    public async Task<IssueMutationResult> UpdateAssigneeWithResultAsync(
        Guid issueId,
        Guid? newAssigneeUserId,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateAssigneeAsync(issueId, newAssigneeUserId, actorUserId, ct);
            return IssueMutationResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Issue {IssueId} not found during UpdateAssignee", issueId);
            return IssueMutationResult.Missing("Issue not found.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update assignee on issue {IssueId}", issueId);
            return IssueMutationResult.Failed("Failed to update assignee.");
        }
    }

    public async Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        if (string.Equals(issue.Section, newSection, StringComparison.Ordinal)) return;

        if (issue.Status.IsTerminal())
        {
            throw new InvalidOperationException(
                $"Cannot change section on a terminal issue (status: {issue.Status}).");
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
        navBadge.Invalidate();
        issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(issue.ReporterUserId, newSection, previousSection, ct));
    }

    public async Task<IssueMutationResult> UpdateSectionWithResultAsync(
        Guid issueId,
        string? newSection,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateSectionAsync(issueId, newSection, actorUserId, ct);
            return IssueMutationResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Issue {IssueId} UpdateSection rejected: {Reason}", issueId, ex.Message);
            return IssueMutationResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update section on issue {IssueId}", issueId);
            return IssueMutationResult.Failed("Failed to update section.");
        }
    }

    public async Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        if (issue.GitHubIssueNumber == githubIssueNumber) return;

        issue.GitHubIssueNumber = githubIssueNumber;
        issue.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveTrackedIssueAsync(issue, ct);

        await LogAuditAsync(
            AuditAction.IssueGitHubLinked, issueId, actorUserId,
            $"GitHub link: {(githubIssueNumber.HasValue ? githubIssueNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(cleared)")}");
    }

    public async Task<IssueMutationResult> SetGitHubIssueNumberWithResultAsync(
        Guid issueId,
        int? githubIssueNumber,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await SetGitHubIssueNumberAsync(issueId, githubIssueNumber, actorUserId, ct);
            return IssueMutationResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Issue {IssueId} not found during SetGitHubIssue", issueId);
            return IssueMutationResult.Missing("Issue not found.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set GitHub issue for issue {IssueId}", issueId);
            return IssueMutationResult.Failed("Failed to link GitHub issue.");
        }
    }

    // ─── Counts & badge/dashboard queries ───

    public async Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.IssuesBadge(viewerUserId);
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BadgeCacheDuration;

            if (viewerIsAdmin) return await repo.CountActionableAsync(null, null, ct);

            var sections = IssueSectionRouting.SectionsForRoles(viewerRoles);
            return await repo.CountActionableAsync(sections, viewerUserId, ct);
        });
    }

    /// <summary>Users whose badge count may shift on an issue mutation: reporter + admins + role-holders for current & previous sections.</summary>
    private async Task<IReadOnlySet<Guid>> ResolveBadgeUserIdsAsync(
        Guid reporterUserId, string? section, string? previousSection,
        CancellationToken ct)
    {
        var ids = new HashSet<Guid> { reporterUserId };

        var admins = await roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct);
        foreach (var id in admins) ids.Add(id);

        foreach (var s in new[] { section, previousSection })
        {
            if (s is null) continue;
            foreach (var role in IssueSectionRouting.RolesFor(s))
            {
                var holders = await roles.GetActiveUserIdsInRoleAsync(role, ct);
                foreach (var id in holders) ids.Add(id);
            }
        }

        return ids;
    }

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
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ─── Retention (daily via CleanupIssuesJob) ───

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - RetentionPeriod;

        var expired = await repo.GetExpiredTerminalAsync(cutoff, ct);
        if (expired.Count == 0) return 0;

        var ids = expired.Select(e => e.Id).ToList();
        var deleted = await repo.DeleteByIdsAsync(ids, ct);

        // Best-effort: delete each issue's wwwroot/uploads/issues/{id}/ dir. Failures logged; next sweep retries.
        foreach (var row in expired)
        {
            var issueDir = Path.Combine(
                env.ContentRootPath, "wwwroot", "uploads", "issues", row.Id.ToString());

            try
            {
                if (Directory.Exists(issueDir))
                    Directory.Delete(issueDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to delete screenshot directory {Dir} for purged issue {IssueId}",
                    issueDir, row.Id);
            }
        }

        logger.LogInformation(
            "PurgeExpiredAsync: deleted {Count} issues older than {Cutoff} (retention {Days}d)",
            deleted, cutoff, RetentionPeriod.Days);

        navBadge.Invalidate();
        return deleted;
    }

    // ─── GDPR contributor ───

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var issues = await repo.GetForUserExportAsync(userId, ct);
        var shaped = issues.Select(i => new
        {
            i.Title,
            i.Description,
            i.Category,
            i.Section,
            i.Status,
            i.PageUrl,
            CreatedAt = i.CreatedAt.ToInvariantInstantString(),
            ResolvedAt = i.ResolvedAt.ToInvariantInstantString(),
            Comments = i.Comments.OrderBy(c => c.CreatedAt).Select(c => new
            {
                c.Content,
                IsFromUser = c.SenderUserId == userId,
                CreatedAt = c.CreatedAt.ToInvariantInstantString()
            })
        }).ToList();

        return [new UserDataSlice(GdprExportSections.Issues, shaped)];
    }

    // ─── Helpers — audit, notifications, in-memory stitching ───

    private async Task LogAuditAsync(
        AuditAction action, Guid issueId, Guid? actorUserId, string description)
    {
        if (actorUserId.HasValue)
        {
            await audit.LogAsync(action, nameof(Issue), issueId, description, actorUserId.Value);
        }
        else
        {
            await audit.LogAsync(action, nameof(Issue), issueId, description, "API");
        }
    }

    private async Task DispatchCommentNotificationsAsync(
        Issue issue, IssueComment comment, bool senderIsReporter, CancellationToken ct)
    {
        var link = $"/Issues/{issue.Id}";
        var subject = $"New comment on issue: {issue.Title}";
        var recipients = new HashSet<Guid>();

        if (senderIsReporter)
        {
            // Reporter commented → notify section role-holders + assignee.
            foreach (var role in IssueSectionRouting.RolesFor(issue.Section))
            {
                foreach (var id in await roles.GetActiveUserIdsInRoleAsync(role, ct))
                {
                    if (id != comment.SenderUserId) recipients.Add(id);
                }
            }
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);
        }
        else
        {
            // Admin/role-holder commented → notify reporter (in-app + email) and assignee.
            if (issue.ReporterUserId != comment.SenderUserId)
                recipients.Add(issue.ReporterUserId);
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);

            await SendCommentEmailAsync(issue, comment, ct);
        }

        if (recipients.Count == 0) return;

        try
        {
            await notifications.SendAsync(
                NotificationSource.IssueComment,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                subject,
                recipients.ToList(),
                body: comment.Content,
                actionUrl: link,
                actionLabel: "View issue",
                cancellationToken: ct);
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
        var reporter = await users.GetUserInfoAsync(issue.ReporterUserId, ct);
        var emails = await userEmails.GetNotificationTargetEmailsAsync(
            [issue.ReporterUserId], ct);

        if (reporter is not null &&
            emails.TryGetValue(issue.ReporterUserId, out var to) &&
            !string.IsNullOrWhiteSpace(to))
        {
            await email.SendIssueCommentAsync(
                to,
                reporter.BurnerName,
                issue.Title,
                comment.Content,
                $"/Issues/{issue.Id}",
                reporter.PreferredLanguage,
                ct);
        }
        else
        {
            logger.LogWarning(
                "Skipping issue comment email for issue {IssueId} — reporter {UserId} has no effective email",
                issue.Id, issue.ReporterUserId);
        }
    }

    private async Task DispatchStatusChangedNotificationAsync(
        Issue issue, IssueStatus oldStatus, IssueStatus newStatus,
        Guid? actorUserId, CancellationToken ct)
    {
        // Notify reporter + assignee (both must differ from the actor). An
        // actor flipping their own issue's status doesn't notify themself.
        var recipients = new HashSet<Guid>();
        if (issue.ReporterUserId != actorUserId) recipients.Add(issue.ReporterUserId);
        if (issue.AssigneeUserId is { } aid && aid != actorUserId) recipients.Add(aid);
        if (recipients.Count == 0) return;

        try
        {
            await notifications.SendAsync(
                NotificationSource.IssueStatusChanged,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Issue status changed: {issue.Title}",
                recipients.ToList(),
                body: $"Status: {oldStatus} → {newStatus}",
                actionUrl: $"/Issues/{issue.Id}",
                actionLabel: "View issue",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueStatusChanged notification for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task DispatchSubmittedNotificationAsync(Issue issue, CancellationToken ct)
    {
        // Fan out to Admins + section role-holders (US-28.4); exclude self-filer.
        var recipients = new HashSet<Guid>();

        foreach (var id in await roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct))
        {
            if (id != issue.ReporterUserId) recipients.Add(id);
        }

        foreach (var role in IssueSectionRouting.RolesFor(issue.Section))
        {
            foreach (var id in await roles.GetActiveUserIdsInRoleAsync(role, ct))
            {
                if (id != issue.ReporterUserId) recipients.Add(id);
            }
        }

        if (recipients.Count == 0) return;

        try
        {
            await notifications.SendAsync(
                NotificationSource.IssueSubmitted,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                $"New issue filed: {issue.Title}",
                recipients.ToList(),
                body: issue.Description,
                actionUrl: $"/Issues/{issue.Id}",
                actionLabel: "View issue",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueSubmitted notification for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task DispatchAssignedNotificationAsync(
        Issue issue, Guid newAssigneeUserId, Guid? actorUserId, CancellationToken ct)
    {
        // Self-assign → no notification.
        if (newAssigneeUserId == actorUserId) return;

        try
        {
            await notifications.SendAsync(
                NotificationSource.IssueAssigned,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                $"You were assigned an issue: {issue.Title}",
                [newAssigneeUserId],
                body: issue.Description,
                actionUrl: $"/Issues/{issue.Id}",
                actionLabel: "View issue",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueAssigned notification for issue {IssueId}",
                issue.Id);
        }
    }

    // In-memory join for [Obsolete] cross-domain nav props (design-rules §6b).
#pragma warning disable CS0618
    private async Task StitchCrossDomainNavsAsync(
        IReadOnlyList<Issue> issues, CancellationToken ct)
    {
        if (issues.Count == 0) return;

        var userIds = new HashSet<Guid>();
        foreach (var i in issues)
        {
            userIds.Add(i.ReporterUserId);
            if (i.AssigneeUserId.HasValue) userIds.Add(i.AssigneeUserId.Value);
            if (i.ResolvedByUserId.HasValue) userIds.Add(i.ResolvedByUserId.Value);
            foreach (var c in i.Comments)
            {
                if (c.SenderUserId.HasValue) userIds.Add(c.SenderUserId.Value);
            }
        }

        var users1 = userIds.Count == 0
            ? null
            : await users.GetByIdsAsync(userIds.ToList(), ct);

        foreach (var i in issues)
        {
            if (users1 is not null && users1.TryGetValue(i.ReporterUserId, out var rep))
                i.Reporter = rep;
            if (i.AssigneeUserId is { } aid && users1 is not null &&
                users1.TryGetValue(aid, out var assignee))
                i.Assignee = assignee;
            if (i.ResolvedByUserId is { } rbid && users1 is not null &&
                users1.TryGetValue(rbid, out var resolver))
                i.ResolvedByUser = resolver;
            foreach (var c in i.Comments)
            {
                if (c.SenderUserId is { } sid && users1 is not null &&
                    users1.TryGetValue(sid, out var sender))
                {
                    c.SenderUser = sender;
                }
            }
        }
    }
#pragma warning restore CS0618
}
