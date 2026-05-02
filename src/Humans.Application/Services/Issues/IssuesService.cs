using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
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
/// Application-layer implementation of <see cref="IIssuesService"/>. All data
/// access flows through <see cref="IIssuesRepository"/>; cross-section reads
/// (reporter/assignee/resolver display names, role holders, effective emails)
/// are routed through <see cref="IUserService"/>,
/// <see cref="IUserEmailService"/>, and <see cref="IRoleAssignmentService"/>.
/// Audit entries are written via <see cref="IAuditLogService"/> after the
/// business save (design-rules §7a). In-app notifications are emitted via
/// <see cref="INotificationService"/>; emails are enqueued via
/// <see cref="IEmailService"/>.
/// </summary>
public sealed class IssuesService : IIssuesService, IUserDataContributor
{
    private readonly IIssuesRepository _repo;
    private readonly IUserService _users;
    private readonly IUserEmailService _userEmails;
    private readonly IRoleAssignmentService _roles;
    private readonly IEmailService _email;
    private readonly INotificationService _notifications;
    private readonly IAuditLogService _audit;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IIssuesBadgeCacheInvalidator _issuesBadge;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly ILogger<IssuesService> _logger;

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

    public IssuesService(
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
        ILogger<IssuesService> logger)
    {
        _repo = repo;
        _users = users;
        _userEmails = userEmails;
        _roles = roles;
        _email = email;
        _notifications = notifications;
        _audit = audit;
        _navBadge = navBadge;
        _issuesBadge = issuesBadge;
        _cache = cache;
        _clock = clock;
        _env = env;
        _logger = logger;
    }

    // ==========================================================================
    // Submission
    // ==========================================================================

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
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var issueId = Guid.NewGuid();

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
            AdditionalContext = additionalContext,
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
            var absolutePath = Path.Combine(_env.ContentRootPath, "wwwroot", relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            await using var stream = new FileStream(absolutePath, FileMode.Create);
            await screenshot.CopyToAsync(stream, ct);

            issue.ScreenshotFileName = screenshot.FileName;
            issue.ScreenshotStoragePath = relativePath.Replace('\\', '/');
            issue.ScreenshotContentType = screenshot.ContentType;
        }

        await _repo.AddIssueAsync(issue, ct);
        _navBadge.Invalidate();
        _issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(reporterUserId, section, null, ct));

        await DispatchSubmittedNotificationAsync(issue, ct);

        _logger.LogInformation(
            "Issue {IssueId} submitted by {UserId}: {Category}/{Section}",
            issueId, reporterUserId, category, section ?? "(unknown)");
        return issue;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default)
    {
        var issue = await _repo.GetByIdAsync(id, ct);
        if (issue is null) return null;
        await StitchCrossDomainNavsAsync([issue], ct);
        return issue;
    }

    public async Task<IReadOnlyList<Issue>> GetIssueListAsync(
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

        var issues = await _repo.GetListAsync(filter, sectionFilter, reporterFallback, ct);
        await StitchCrossDomainNavsAsync(issues, ct);
        return issues;
    }

    public async Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, CancellationToken ct = default)
    {
        var issue = await GetIssueByIdAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        // Pull the relevant audit entries via the existing filtered-entries
        // method on IAuditLogService — we filter by entity + the four
        // Issue-related actions.
        var auditEntries = await _audit.GetFilteredEntriesAsync(
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

        // Resolve actor display names for the audit entries via IUserService
        // (audit log doesn't expose ActorDisplayName on its DTO).
        var actorIds = auditEntries
            .Where(a => a.ActorUserId.HasValue)
            .Select(a => a.ActorUserId!.Value)
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, User> actorUsers = actorIds.Count == 0
            ? new Dictionary<Guid, User>()
            : await _users.GetByIdsAsync(actorIds, ct);

#pragma warning disable CS0618 // Cross-domain nav populated in memory
        var commentEvents = issue.Comments.Select(c => (IssueThreadEvent)new IssueCommentEvent(
            c.Id,
            c.CreatedAt,
            c.SenderUserId,
            c.SenderUser?.DisplayName,
            ActorIsReporter: c.SenderUserId.HasValue && c.SenderUserId == issue.ReporterUserId,
            c.Content));
#pragma warning restore CS0618

        var auditEvents = auditEntries.Select(a => (IssueThreadEvent)new IssueAuditEvent(
            a.OccurredAt,
            a.ActorUserId,
            a.ActorUserId.HasValue && actorUsers.TryGetValue(a.ActorUserId.Value, out var u)
                ? u.DisplayName
                : null,
            a.Action,
            a.Description));

        return commentEvents
            .Concat(auditEvents)
            .OrderBy(e => e.At)
            .ToList();
    }

    // ==========================================================================
    // Mutations
    // ==========================================================================

    public async Task<IssueComment> PostCommentAsync(
        Guid issueId,
        Guid? senderUserId,
        string content,
        bool senderIsReporter,
        CancellationToken ct = default)
    {
        var issue = await _repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        var now = _clock.GetCurrentInstant();
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
        await _repo.AddCommentAndSaveIssueAsync(comment, issue, ct);

        if (statusChangedToOpen)
        {
            await LogAuditAsync(
                AuditAction.IssueStatusChanged, issueId, senderUserId,
                "reopened (reporter comment on terminal)");
        }

        await DispatchCommentNotificationsAsync(issue, comment, senderIsReporter, ct);
        _navBadge.Invalidate();
        if (statusChangedToOpen)
        {
            _issuesBadge.InvalidateMany(
                await ResolveBadgeUserIdsAsync(issue.ReporterUserId, issue.Section, null, ct));
        }

        _logger.LogInformation(
            "Comment posted on issue {IssueId} by {UserId} (reporter: {Reporter})",
            issueId, senderUserId, senderIsReporter);
        return comment;
    }

    public async Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await _repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        var oldStatus = issue.Status;
        if (oldStatus == newStatus) return;

        var now = _clock.GetCurrentInstant();
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

        await _repo.SaveTrackedIssueAsync(issue, ct);
        await LogAuditAsync(
            AuditAction.IssueStatusChanged, issueId, actorUserId,
            $"status: {oldStatus} → {newStatus}");
        await DispatchStatusChangedNotificationAsync(issue, oldStatus, newStatus, actorUserId, ct);
        _navBadge.Invalidate();
        _issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(issue.ReporterUserId, issue.Section, null, ct));
    }

    public async Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await _repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        if (issue.AssigneeUserId == newAssigneeUserId) return;

        var oldAssigneeId = issue.AssigneeUserId;
        var idsToResolve = new List<Guid>(2);
        if (oldAssigneeId.HasValue) idsToResolve.Add(oldAssigneeId.Value);
        if (newAssigneeUserId.HasValue) idsToResolve.Add(newAssigneeUserId.Value);

        var users = idsToResolve.Count == 0
            ? null
            : await _users.GetByIdsAsync(idsToResolve, ct);

        var oldName = oldAssigneeId.HasValue
            ? (users!.TryGetValue(oldAssigneeId.Value, out var ou) ? ou.DisplayName : oldAssigneeId.Value.ToString())
            : "Unassigned";
        var newName = newAssigneeUserId.HasValue
            ? (users!.TryGetValue(newAssigneeUserId.Value, out var nu) ? nu.DisplayName : newAssigneeUserId.Value.ToString())
            : "Unassigned";

        issue.AssigneeUserId = newAssigneeUserId;
        issue.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.SaveTrackedIssueAsync(issue, ct);

        await LogAuditAsync(
            AuditAction.IssueAssigneeChanged, issueId, actorUserId,
            $"assignee: {oldName} → {newName}");

        if (newAssigneeUserId.HasValue)
        {
            await DispatchAssignedNotificationAsync(issue, newAssigneeUserId.Value, actorUserId, ct);
        }
    }

    public async Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await _repo.FindForMutationAsync(issueId, ct)
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
        issue.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.SaveTrackedIssueAsync(issue, ct);

        await LogAuditAsync(
            AuditAction.IssueSectionChanged, issueId, actorUserId,
            $"section: {oldSection} → {nextSection}");
        _navBadge.Invalidate();
        _issuesBadge.InvalidateMany(
            await ResolveBadgeUserIdsAsync(issue.ReporterUserId, newSection, previousSection, ct));
    }

    public async Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId,
        CancellationToken ct = default)
    {
        var issue = await _repo.FindForMutationAsync(issueId, ct)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");

        if (issue.GitHubIssueNumber == githubIssueNumber) return;

        issue.GitHubIssueNumber = githubIssueNumber;
        issue.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.SaveTrackedIssueAsync(issue, ct);

        await LogAuditAsync(
            AuditAction.IssueGitHubLinked, issueId, actorUserId,
            $"GitHub link: {(githubIssueNumber.HasValue ? githubIssueNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(cleared)")}");
    }

    // ==========================================================================
    // Counts and queries used by nav badge / dashboards
    // ==========================================================================

    public async Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.IssuesBadge(viewerUserId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BadgeCacheDuration;

            if (viewerIsAdmin) return await _repo.CountActionableAsync(null, null, ct);

            var sections = IssueSectionRouting.SectionsForRoles(viewerRoles);
            return await _repo.CountActionableAsync(sections, viewerUserId, ct);
        });
    }

    /// <summary>
    /// Returns every user whose actionable-issues badge count may have shifted
    /// because of a mutation on an issue with the given (and optionally
    /// previous) section: the reporter, every active Admin, and every
    /// role-holder for the section's owning role(s). Empty sections fall back
    /// to admins-only routing per <see cref="IssueSectionRouting"/>.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveBadgeUserIdsAsync(
        Guid reporterUserId, string? section, string? previousSection,
        CancellationToken ct)
    {
        var ids = new HashSet<Guid> { reporterUserId };

        var admins = await _roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct);
        foreach (var id in admins) ids.Add(id);

        foreach (var s in new[] { section, previousSection })
        {
            if (s is null) continue;
            foreach (var role in IssueSectionRouting.RolesFor(s))
            {
                var holders = await _roles.GetActiveUserIdsInRoleAsync(role, ct);
                foreach (var id in holders) ids.Add(id);
            }
        }

        return ids;
    }

    public async Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(
        CancellationToken ct = default)
    {
        var rows = await _repo.GetReporterCountsAsync(ct);
        if (rows.Count == 0) return [];

        var users = await _users.GetByIdsAsync(rows.Select(r => r.UserId).ToList(), ct);
        return rows
            .Select(r => new DistinctReporterRow(
                r.UserId,
                users.TryGetValue(r.UserId, out var u) ? u.DisplayName : r.UserId.ToString(),
                r.Count))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ==========================================================================
    // Retention — invoked daily by CleanupIssuesJob
    // ==========================================================================

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = _clock.GetCurrentInstant() - RetentionPeriod;

        var expired = await _repo.GetExpiredTerminalAsync(cutoff, ct);
        if (expired.Count == 0) return 0;

        var ids = expired.Select(e => e.Id).ToList();
        var deleted = await _repo.DeleteByIdsAsync(ids, ct);

        // Best-effort filesystem cleanup. Each issue's screenshots live under
        // wwwroot/uploads/issues/{id}/. We delete the whole directory rather
        // than file-by-file so a stray sibling (e.g. partial upload) is also
        // swept. Failures are logged but do not roll back the DB delete — the
        // worst case is an orphan file the next sweep will see again.
        foreach (var row in expired)
        {
            var issueDir = Path.Combine(
                _env.ContentRootPath, "wwwroot", "uploads", "issues", row.Id.ToString());

            try
            {
                if (Directory.Exists(issueDir))
                    Directory.Delete(issueDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete screenshot directory {Dir} for purged issue {IssueId}",
                    issueDir, row.Id);
            }
        }

        _logger.LogInformation(
            "PurgeExpiredAsync: deleted {Count} issues older than {Cutoff} (retention {Days}d)",
            deleted, cutoff, RetentionPeriod.Days);

        _navBadge.Invalidate();
        return deleted;
    }

    // ==========================================================================
    // GDPR contributor
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var issues = await _repo.GetForUserExportAsync(userId, ct);
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

    // ==========================================================================
    // Helpers — audit, notifications, in-memory stitching
    // ==========================================================================

    private async Task LogAuditAsync(
        AuditAction action, Guid issueId, Guid? actorUserId, string description)
    {
        if (actorUserId.HasValue)
        {
            await _audit.LogAsync(action, nameof(Issue), issueId, description, actorUserId.Value);
        }
        else
        {
            await _audit.LogAsync(action, nameof(Issue), issueId, description, "API");
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
            // Reporter commented → notify role holders for the issue's section
            // plus the assignee (if any). IRoleAssignmentService exposes a
            // singular role lookup; iterate over the section's roles and union.
            foreach (var role in IssueSectionRouting.RolesFor(issue.Section))
            {
                foreach (var id in await _roles.GetActiveUserIdsInRoleAsync(role, ct))
                {
                    if (id != comment.SenderUserId) recipients.Add(id);
                }
            }
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);
        }
        else
        {
            // Admin/role-holder commented → notify the reporter (in-app + email)
            // and the assignee if different.
            if (issue.ReporterUserId != comment.SenderUserId)
                recipients.Add(issue.ReporterUserId);
            if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
                recipients.Add(aid);

            await SendCommentEmailAsync(issue, comment, ct);
        }

        if (recipients.Count == 0) return;

        try
        {
            await _notifications.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch IssueComment notifications for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task SendCommentEmailAsync(Issue issue, IssueComment comment, CancellationToken ct)
    {
        var reporter = await _users.GetByIdAsync(issue.ReporterUserId, ct);
        var emails = await _userEmails.GetNotificationTargetEmailsAsync(
            [issue.ReporterUserId], ct);

        if (reporter is not null &&
            emails.TryGetValue(issue.ReporterUserId, out var to) &&
            !string.IsNullOrWhiteSpace(to))
        {
            await _email.SendIssueCommentAsync(
                to,
                reporter.DisplayName,
                issue.Title,
                comment.Content,
                $"/Issues/{issue.Id}",
                reporter.PreferredLanguage,
                ct);
        }
        else
        {
            _logger.LogWarning(
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
            await _notifications.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch IssueStatusChanged notification for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task DispatchSubmittedNotificationAsync(Issue issue, CancellationToken ct)
    {
        // Fan out to Admins + section role-holders so handlers get an in-app
        // ping instead of relying on the nav-badge alone (per
        // docs/features/28-issues-system.md US-28.4). The reporter is
        // excluded so a handler filing their own issue doesn't notify
        // themselves.
        var recipients = new HashSet<Guid>();

        foreach (var id in await _roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, ct))
        {
            if (id != issue.ReporterUserId) recipients.Add(id);
        }

        foreach (var role in IssueSectionRouting.RolesFor(issue.Section))
        {
            foreach (var id in await _roles.GetActiveUserIdsInRoleAsync(role, ct))
            {
                if (id != issue.ReporterUserId) recipients.Add(id);
            }
        }

        if (recipients.Count == 0) return;

        try
        {
            await _notifications.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch IssueSubmitted notification for issue {IssueId}",
                issue.Id);
        }
    }

    private async Task DispatchAssignedNotificationAsync(
        Issue issue, Guid newAssigneeUserId, Guid? actorUserId, CancellationToken ct)
    {
        // Self-assign is a no-op for notifications — the actor doesn't need
        // a "you assigned yourself" alert. Mirrors the actor-exclusion pattern
        // in DispatchCommentNotificationsAsync / DispatchStatusChangedNotificationAsync.
        if (newAssigneeUserId == actorUserId) return;

        try
        {
            await _notifications.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch IssueAssigned notification for issue {IssueId}",
                issue.Id);
        }
    }

    // Cross-domain nav stitching — populates [Obsolete]-marked nav properties
    // in memory from service calls (design-rules §6b "in-memory join").
#pragma warning disable CS0618 // Cross-domain nav properties populated in-memory
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

        var users = userIds.Count == 0
            ? null
            : await _users.GetByIdsAsync(userIds.ToList(), ct);

        foreach (var i in issues)
        {
            if (users is not null && users.TryGetValue(i.ReporterUserId, out var rep))
                i.Reporter = rep;
            if (i.AssigneeUserId is { } aid && users is not null &&
                users.TryGetValue(aid, out var assignee))
                i.Assignee = assignee;
            if (i.ResolvedByUserId is { } rbid && users is not null &&
                users.TryGetValue(rbid, out var resolver))
                i.ResolvedByUser = resolver;
            foreach (var c in i.Comments)
            {
                if (c.SenderUserId is { } sid && users is not null &&
                    users.TryGetValue(sid, out var sender))
                {
                    c.SenderUser = sender;
                }
            }
        }
    }
#pragma warning restore CS0618
}
