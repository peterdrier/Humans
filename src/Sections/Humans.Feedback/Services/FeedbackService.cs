using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Domain.Enums;
using Humans.Application;
using Humans.Feedback.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Humans.Feedback.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Notifications.Contracts;
using Humans.Application.Interfaces.Profiles;
using Humans.Users.Contracts;

namespace Humans.Feedback.Services;

/// <summary>
/// The Feedback section's service. Admin triage over the historical rows —
/// Feedback stopped accepting new reports in nobodies-collective/Humans#977, so
/// there is deliberately no create path.
/// Cross-section reads (display names, team names, effective emails) go through
/// <see cref="IUserService"/>, <see cref="ITeamServiceRead"/>, and
/// <see cref="IUserEmailService"/> and are projected into
/// <see cref="FeedbackReportInfo"/> / <see cref="FeedbackMessageInfo"/>.
/// Nav-badge invalidation routes through <see cref="INavBadgeCacheInvalidator"/>.
/// </summary>
internal sealed class FeedbackService(
    IFeedbackRepository repository,
    IUserServiceRead userService,
    IUserEmailService userEmailService,
    ITeamServiceRead teamService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    INotificationEmitter notificationService,
    IAuditLogService auditLogService,
    INavBadgeCacheInvalidator navBadge,
    IMemoryCache cache,
    IClock clock,
    ILogger<FeedbackService> logger) : IFeedbackServiceRead, IUserDataContributor, IUserMerge
{
    private static readonly TimeSpan BadgeCacheDuration = TimeSpan.FromMinutes(2);

    public async Task<FeedbackReportInfo?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var report = await repository.GetByIdAsync(id, cancellationToken);
        if (report is null) return null;

        var lookups = await StitchCrossDomainNavsAsync([report], cancellationToken);
        return CreateFeedbackReportInfo(report, lookups);
    }

    public async Task<IReadOnlyList<FeedbackReportInfo>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, Guid? assignedToUserId = null,
        Guid? assignedToTeamId = null, bool? unassignedOnly = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var reports = await repository.GetListAsync(
            status, category, reporterUserId, assignedToUserId, assignedToTeamId,
            unassignedOnly, limit, cancellationToken);

        var lookups = await StitchCrossDomainNavsAsync(reports, cancellationToken);
        return reports.Select(r => CreateFeedbackReportInfo(r, lookups)).ToList();
    }

    public async Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var now = clock.GetCurrentInstant();
        report.Status = status;
        report.UpdatedAt = now;

        if (status is FeedbackStatus.Resolved or FeedbackStatus.WontFix)
        {
            report.ResolvedAt = now;
            report.ResolvedByUserId = actorUserId;
        }
        else
        {
            report.ResolvedAt = null;
            report.ResolvedByUserId = null;
        }

        await repository.SaveTrackedReportAsync(report, cancellationToken);

        // Audit after the business save so a rollback never leaves a ghost audit row.
        if (actorUserId.HasValue)
        {
            await auditLogService.LogAsync(
                AuditAction.FeedbackStatusChanged, AuditEntityTypes.FeedbackReport, id,
                $"Feedback {id} status changed to {status}",
                actorUserId.Value);
        }
        else
        {
            await auditLogService.LogAsync(
                AuditAction.FeedbackStatusChanged, AuditEntityTypes.FeedbackReport, id,
                $"Feedback {id} status changed to {status}",
                "API");
        }

        navBadge.Invalidate();
    }

    public async Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default)
    {
        var report = await repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        report.GitHubIssueNumber = issueNumber;
        report.UpdatedAt = clock.GetCurrentInstant();

        await repository.SaveTrackedReportAsync(report, cancellationToken);
    }

    public async Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content,
        CancellationToken cancellationToken = default)
    {
        var report = await repository.FindForMutationAsync(reportId, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {reportId} not found");

        var now = clock.GetCurrentInstant();
        var message = new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = senderUserId,
            Content = content,
            CreatedAt = now
        };

        report.LastAdminMessageAt = now;
        report.UpdatedAt = now;

        // Send email BEFORE persisting so an SMTP throw leaves no committed message → safe to retry.
        await SendAdminResponseEmailAsync(report, content, cancellationToken);

        await repository.AddMessageAndSaveReportAsync(message, report, cancellationToken);

        await DispatchAdminReplyNotificationAsync(report, cancellationToken);

        navBadge.Invalidate();
        logger.LogInformation(
            "Feedback admin reply posted on {ReportId} by {UserId}", reportId, senderUserId);
        return message;
    }

    private async Task SendAdminResponseEmailAsync(
        FeedbackReport report, string content, CancellationToken ct)
    {
        var reportLink = $"/Feedback/{report.Id}";

        var reporter = await userService.GetUserInfoAsync(report.UserId, ct);
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(
            [report.UserId], ct);

        if (reporter is not null && emails.TryGetValue(report.UserId, out var recipientEmail) &&
            !string.IsNullOrWhiteSpace(recipientEmail))
        {
            await emailService.SendAsync(emailMessages.FeedbackResponse(
                recipientEmail, reporter.BurnerName,
                report.Description, content, reportLink,
                reporter.PreferredLanguage), ct);
        }
        else
        {
            logger.LogWarning(
                "Skipping feedback response email for report {ReportId} because user {UserId} has no effective email",
                report.Id, report.UserId);
        }
    }

    private async Task DispatchAdminReplyNotificationAsync(
        FeedbackReport report, CancellationToken ct)
    {
        try
        {
            // No action link: /Feedback/{id} is Admin-only now, so the reporter would land on a 403.
            // The response text itself reaches them in the FeedbackResponse email.
            await notificationService.SendAsync(
                NotificationSource.FeedbackResponse,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "You have a response to your feedback",
                [report.UserId],
                body: "An admin has responded to your feedback report.",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch FeedbackResponse notification for report {ReportId}", report.Id);
        }
    }

    public async Task UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var changes = new List<string>();

        // Capture previous assignee/team names for the audit description.
        string? oldAssigneeName = null;
        string? oldTeamName = null;

        if (report.AssignedToUserId != assignedToUserId && report.AssignedToUserId.HasValue)
        {
            var old = await userService.GetUserInfoAsync(report.AssignedToUserId.Value, cancellationToken);
            oldAssigneeName = old?.BurnerName;
        }

        if (report.AssignedToTeamId != assignedToTeamId && report.AssignedToTeamId.HasValue)
        {
            var team = await teamService.GetTeamAsync(report.AssignedToTeamId.Value, cancellationToken);
            oldTeamName = team?.Name;
        }

        if (report.AssignedToUserId != assignedToUserId)
        {
            var fromLabel = oldAssigneeName ?? "Unassigned";
            string toLabel = "Unassigned";
            if (assignedToUserId.HasValue)
            {
                var newUser = await userService.GetUserInfoAsync(assignedToUserId.Value, cancellationToken);
                toLabel = newUser?.BurnerName ?? assignedToUserId.Value.ToString();
            }
            changes.Add($"Assignee: {fromLabel} → {toLabel}");
            report.AssignedToUserId = assignedToUserId;
        }

        if (report.AssignedToTeamId != assignedToTeamId)
        {
            var fromLabel = oldTeamName ?? "Unassigned";
            string toLabel = "Unassigned";
            if (assignedToTeamId.HasValue)
            {
                var team = await teamService.GetTeamAsync(assignedToTeamId.Value, cancellationToken);
                toLabel = team?.Name ?? assignedToTeamId.Value.ToString();
            }
            changes.Add($"Team: {fromLabel} → {toLabel}");
            report.AssignedToTeamId = assignedToTeamId;
        }

        if (changes.Count == 0)
            return;

        report.UpdatedAt = clock.GetCurrentInstant();

        await repository.SaveTrackedReportAsync(report, cancellationToken);

        // Audit after the business save so a rollback never leaves a ghost audit row.
        var description = $"Feedback {id} assignment changed: {string.Join("; ", changes)}";
        if (actorUserId.HasValue)
        {
            await auditLogService.LogAsync(
                AuditAction.FeedbackAssignmentChanged, AuditEntityTypes.FeedbackReport, id,
                description, actorUserId.Value);
        }
        else
        {
            await auditLogService.LogAsync(
                AuditAction.FeedbackAssignmentChanged, AuditEntityTypes.FeedbackReport, id,
                description, "API");
        }

        logger.LogInformation(
            "Feedback {ReportId} assignment updated by {ActorId}: {Changes}",
            id, actorUserId?.ToString() ?? "API", string.Join("; ", changes));
    }

    public async Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(CacheKeys.FeedbackBadgeCount, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BadgeCacheDuration;
            return await repository.GetActionableCountAsync(cancellationToken);
        });

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, int Count)>> GetDistinctReportersAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetReporterCountsAsync(cancellationToken);
        if (rows.Count == 0)
            return [];

        var userIds = rows.Select(r => r.UserId).ToHashSet();
        var displayUsers = await BuildDisplayUsersAsync(userIds, cancellationToken);

        return rows
            .Select(r =>
            {
                var name = displayUsers.TryGetValue(r.UserId, out var displayUser) ? displayUser.Name : r.UserId.ToString();
                return (r.UserId, name, r.Count);
            })
            .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
        => repository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

    public async Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var reports = await repository.GetListAsync(
            status: FeedbackStatus.Open,
            category: null,
            reporterUserId: userId,
            assignedToUserId: null,
            assignedToTeamId: null,
            unassignedOnly: null,
            limit: int.MaxValue,
            ct: cancellationToken);
        return reports.Select(r => r.Id).ToList();
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var reports = await repository.GetForUserExportAsync(userId, ct);

        var shaped = reports
            .OrderByDescending(fr => fr.CreatedAt)
            .Select(fr => new
            {
                fr.Category,
                fr.Description,
                fr.PageUrl,
                fr.Status,
                CreatedAt = fr.CreatedAt.ToIso8601(),
                ResolvedAt = fr.ResolvedAt.ToIso8601(),
                Messages = fr.Messages.OrderBy(m => m.CreatedAt).Select(m => new
                {
                    m.Content,
                    IsFromUser = m.SenderUserId == userId,
                    CreatedAt = m.CreatedAt.ToIso8601()
                })
            }).ToList();

        return [new UserDataSlice(GdprExportSections.FeedbackReports, shaped)];
    }

    // Cross-domain read stitching (design-rules §6b in-memory join via UserInfo).
    private async Task<FeedbackCrossDomainLookups> StitchCrossDomainNavsAsync(
        IReadOnlyList<FeedbackReport> reports, CancellationToken ct)
    {
        if (reports.Count == 0) return EmptyFeedbackCrossDomainLookups;

        var userIds = new HashSet<Guid>();
        var teamIds = new HashSet<Guid>();

        foreach (var r in reports)
        {
            userIds.Add(r.UserId);
            if (r.ResolvedByUserId.HasValue) userIds.Add(r.ResolvedByUserId.Value);
            if (r.AssignedToUserId.HasValue) userIds.Add(r.AssignedToUserId.Value);
            if (r.AssignedToTeamId.HasValue) teamIds.Add(r.AssignedToTeamId.Value);

            foreach (var m in r.Messages)
            {
                if (m.SenderUserId.HasValue) userIds.Add(m.SenderUserId.Value);
            }
        }

        IReadOnlyDictionary<Guid, string> teamNames = EmptyTeamNames;
        if (teamIds.Count > 0)
        {
            var teamsById = await teamService.GetTeamsAsync(ct);
            teamNames = teamIds
                .Where(teamsById.ContainsKey)
                .ToDictionary(id => id, id => teamsById[id].Name);
        }

        return new FeedbackCrossDomainLookups(
            await BuildDisplayUsersAsync(userIds, ct),
            teamNames);
    }

    // Display/user-facing identity via UserInfo (memory/architecture/burnername-is-the-display-name.md).
    private async Task<IReadOnlyDictionary<Guid, DisplayUser>> BuildDisplayUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return EmptyDisplayUsers;

        var infos = await userService.GetUserInfosAsync(userIds, ct);
        var result = new Dictionary<Guid, DisplayUser>(userIds.Count);
        foreach (var id in userIds)
        {
            if (infos.TryGetValue(id, out var info))
                result[id] = new DisplayUser(info.BurnerName, info.Email, info.PreferredLanguage);
        }
        return result;
    }

    private sealed record DisplayUser(string Name, string? Email, string PreferredLanguage);

    private sealed record FeedbackCrossDomainLookups(
        IReadOnlyDictionary<Guid, DisplayUser> DisplayUsers,
        IReadOnlyDictionary<Guid, string> TeamNames);

    private static readonly IReadOnlyDictionary<Guid, DisplayUser> EmptyDisplayUsers =
        new Dictionary<Guid, DisplayUser>();

    private static readonly IReadOnlyDictionary<Guid, string> EmptyTeamNames =
        new Dictionary<Guid, string>();

    private static readonly FeedbackCrossDomainLookups EmptyFeedbackCrossDomainLookups =
        new(EmptyDisplayUsers, EmptyTeamNames);

    private static FeedbackReportInfo CreateFeedbackReportInfo(
        FeedbackReport report,
        FeedbackCrossDomainLookups lookups) =>
        new(
            report.Id,
            report.UserId,
            report.Category,
            report.Description,
            report.PageUrl,
            report.UserAgent,
            report.AdditionalContext,
            report.ScreenshotStoragePath,
            report.Status,
            report.GitHubIssueNumber,
            report.LastReporterMessageAt,
            report.LastAdminMessageAt,
            NeedsReply(report),
            report.CreatedAt,
            report.UpdatedAt,
            report.ResolvedAt,
            report.ResolvedByUserId,
            report.AssignedToUserId,
            report.AssignedToTeamId,
            lookups.DisplayUsers.TryGetValue(report.UserId, out var reporter) ? reporter.Name : report.UserId.ToString(),
            reporter?.Email,
            reporter?.PreferredLanguage ?? "en",
            ResolveName(report.ResolvedByUserId, lookups.DisplayUsers),
            ResolveName(report.AssignedToUserId, lookups.DisplayUsers),
            ResolveTeamName(report.AssignedToTeamId, lookups.TeamNames),
            report.Messages.Select(m => CreateFeedbackMessageInfo(m, lookups.DisplayUsers)).ToList());

    private static bool NeedsReply(FeedbackReport report) =>
        (report.LastReporterMessageAt.HasValue &&
            (!report.LastAdminMessageAt.HasValue || report.LastReporterMessageAt > report.LastAdminMessageAt)) ||
        (report.Status == FeedbackStatus.Open && !report.LastAdminMessageAt.HasValue);

    private static FeedbackMessageInfo CreateFeedbackMessageInfo(
        FeedbackMessage message,
        IReadOnlyDictionary<Guid, DisplayUser> displayUsers) =>
        new(
            message.Id,
            message.FeedbackReportId,
            message.SenderUserId,
            ResolveName(message.SenderUserId, displayUsers),
            message.Content,
            message.CreatedAt);

    private static string? ResolveName(Guid? userId, IReadOnlyDictionary<Guid, DisplayUser> displayUsers)
        => userId.HasValue && displayUsers.TryGetValue(userId.Value, out var user) ? user.Name : null;

    private static string? ResolveTeamName(Guid? teamId, IReadOnlyDictionary<Guid, string> teamNames)
        => teamId.HasValue && teamNames.TryGetValue(teamId.Value, out var teamName) ? teamName : null;
}
