using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Models;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Feedback;

/// <summary>
/// Application-layer implementation of <see cref="IFeedbackService"/>. Goes
/// through <see cref="IFeedbackRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph. Cross-section reads
/// (reporter/assignee/resolver display names, team names, effective emails)
/// go through <see cref="IUserService"/>, <see cref="ITeamService"/>, and
/// <see cref="IUserEmailService"/>. Nav-badge cache invalidation is routed
/// through <see cref="INavBadgeCacheInvalidator"/> rather than
/// <c>IMemoryCache</c> directly.
/// </summary>
/// <remarks>
/// Feedback is admin-review-only and low-traffic, so no caching decorator
/// sits in front of this service. The service stitches display data in
/// memory onto the <see cref="FeedbackReport"/> entity's (now <c>[Obsolete]</c>)
/// cross-domain navigation properties so existing controllers and views can
/// continue to read <c>report.User.DisplayName</c>, <c>report.AssignedToTeam.Name</c>,
/// etc. without change — this is the "in-memory join" from design-rules §6b.
/// </remarks>
public sealed class FeedbackService : IFeedbackService, IUserDataContributor
{
    private readonly IFeedbackRepository _repository;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IAuditLogService _auditLogService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly ILogger<FeedbackService> _logger;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private const long MaxScreenshotBytes = 10 * 1024 * 1024; // 10MB

    public FeedbackService(
        IFeedbackRepository repository,
        IUserService userService,
        IUserEmailService userEmailService,
        ITeamService teamService,
        IEmailService emailService,
        INotificationService notificationService,
        IAuditLogService auditLogService,
        INavBadgeCacheInvalidator navBadge,
        IClock clock,
        IHostEnvironment env,
        ILogger<FeedbackService> logger)
    {
        _repository = repository;
        _userService = userService;
        _userEmailService = userEmailService;
        _teamService = teamService;
        _emailService = emailService;
        _notificationService = notificationService;
        _auditLogService = auditLogService;
        _navBadge = navBadge;
        _clock = clock;
        _env = env;
        _logger = logger;
    }

    public async Task<FeedbackReport> SubmitFeedbackAsync(
        Guid userId, FeedbackCategory category, string description,
        string pageUrl, string? userAgent, string? additionalContext,
        IFormFile? screenshot, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var reportId = Guid.NewGuid();

        var report = new FeedbackReport
        {
            Id = reportId,
            UserId = userId,
            Category = category,
            Description = description,
            PageUrl = pageUrl,
            UserAgent = userAgent,
            AdditionalContext = additionalContext,
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Handle screenshot upload.
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
            var relativePath = Path.Combine("uploads", "feedback", reportId.ToString(), fileName);
            var absolutePath = Path.Combine(_env.ContentRootPath, "wwwroot", relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            await using var stream = new FileStream(absolutePath, FileMode.Create);
            await screenshot.CopyToAsync(stream, cancellationToken);

            report.ScreenshotFileName = screenshot.FileName;
            report.ScreenshotStoragePath = relativePath.Replace('\\', '/');
            report.ScreenshotContentType = screenshot.ContentType;
        }

        await _repository.AddReportAsync(report, cancellationToken);
        _navBadge.Invalidate();

        _logger.LogInformation("Feedback {ReportId} submitted by {UserId}: {Category}", reportId, userId, category);

        return report;
    }

    public async Task<FeedbackReport?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var report = await _repository.GetByIdAsync(id, cancellationToken);
        if (report is null) return null;

        await StitchCrossDomainNavsAsync([report], cancellationToken);
        return report;
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, Guid? assignedToUserId = null,
        Guid? assignedToTeamId = null, bool? unassignedOnly = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var reports = await _repository.GetListAsync(
            status, category, reporterUserId, assignedToUserId, assignedToTeamId,
            unassignedOnly, limit, cancellationToken);

        await StitchCrossDomainNavsAsync(reports, cancellationToken);
        return reports;
    }

    public async Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await _repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var now = _clock.GetCurrentInstant();
        report.Status = status;
        report.UpdatedAt = now;

        if (status is FeedbackStatus.Resolved or FeedbackStatus.WontFix)
        {
            report.ResolvedAt = now;
            report.ResolvedByUserId = actorUserId;
        }
        else
        {
            // Reopening — clear resolved fields.
            report.ResolvedAt = null;
            report.ResolvedByUserId = null;
        }

        await _repository.SaveTrackedReportAsync(report, cancellationToken);

        // Audit after the business save so a rollback never leaves a ghost audit row.
        if (actorUserId.HasValue)
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackStatusChanged, nameof(FeedbackReport), id,
                $"Feedback {id} status changed to {status}",
                actorUserId.Value);
        }
        else
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackStatusChanged, nameof(FeedbackReport), id,
                $"Feedback {id} status changed to {status}",
                "API");
        }

        _navBadge.Invalidate();
    }

    public async Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default)
    {
        var report = await _repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        report.GitHubIssueNumber = issueNumber;
        report.UpdatedAt = _clock.GetCurrentInstant();

        await _repository.SaveTrackedReportAsync(report, cancellationToken);
    }

    public async Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content, bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var report = await _repository.FindForMutationAsync(reportId, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {reportId} not found");

        var now = _clock.GetCurrentInstant();
        var message = new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = senderUserId,
            Content = content,
            CreatedAt = now
        };

        if (isAdmin)
        {
            report.LastAdminMessageAt = now;
        }
        else
        {
            report.LastReporterMessageAt = now;
        }

        report.UpdatedAt = now;

        // Admin replies: send the response email BEFORE persisting. If SMTP
        // throws, the new message and LastAdminMessageAt timestamp are never
        // committed, so the request returns an error and can be safely retried
        // without duplicating the admin message. The in-app notification stays
        // post-save as a best-effort side effect.
        if (isAdmin)
        {
            await SendAdminResponseEmailAsync(report, content, cancellationToken);
        }

        await _repository.AddMessageAndSaveReportAsync(message, report, cancellationToken);

        if (isAdmin)
        {
            await DispatchAdminReplyNotificationAsync(report, cancellationToken);
        }

        _navBadge.Invalidate();
        _logger.LogInformation(
            "Feedback message posted on {ReportId} by {UserId} (admin: {IsAdmin})",
            reportId, senderUserId, isAdmin);
        return message;
    }

    private async Task SendAdminResponseEmailAsync(
        FeedbackReport report, string content, CancellationToken ct)
    {
        var reportLink = $"/Feedback/{report.Id}";

        // Resolve the reporter user and their effective email via the services
        // that own that data (no cross-domain navigation).
        var reporter = await _userService.GetByIdAsync(report.UserId, ct);
        var emails = await _userEmailService.GetNotificationTargetEmailsAsync(
            [report.UserId], ct);

        if (reporter is not null && emails.TryGetValue(report.UserId, out var recipientEmail) &&
            !string.IsNullOrWhiteSpace(recipientEmail))
        {
            await _emailService.SendFeedbackResponseAsync(
                recipientEmail, reporter.DisplayName,
                report.Description, content, reportLink,
                reporter.PreferredLanguage, ct);
        }
        else
        {
            _logger.LogWarning(
                "Skipping feedback response email for report {ReportId} because user {UserId} has no effective email",
                report.Id, report.UserId);
        }
    }

    private async Task DispatchAdminReplyNotificationAsync(
        FeedbackReport report, CancellationToken ct)
    {
        var reportLink = $"/Feedback/{report.Id}";

        try
        {
            await _notificationService.SendAsync(
                NotificationSource.FeedbackResponse,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "You have a response to your feedback",
                [report.UserId],
                body: "An admin has responded to your feedback report.",
                actionUrl: reportLink,
                actionLabel: "View response",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch FeedbackResponse notification for report {ReportId}", report.Id);
        }
    }

    public async Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken cancellationToken = default)
    {
        var messages = await _repository.GetMessagesAsync(reportId, cancellationToken);
        await StitchMessageSendersAsync(messages, cancellationToken);
        return messages;
    }

    public async Task UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await _repository.FindForMutationAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var changes = new List<string>();

        // Capture previous assignee/team for the audit description. Resolve
        // the old name via the services since the tracked entity has no nav
        // included.
        string? oldAssigneeName = null;
        string? oldTeamName = null;

        if (report.AssignedToUserId != assignedToUserId && report.AssignedToUserId.HasValue)
        {
            var old = await _userService.GetByIdAsync(report.AssignedToUserId.Value, cancellationToken);
            oldAssigneeName = old?.DisplayName;
        }

        if (report.AssignedToTeamId != assignedToTeamId && report.AssignedToTeamId.HasValue)
        {
            var oldTeam = await _teamService.GetTeamNamesByIdsAsync(
                [report.AssignedToTeamId.Value], cancellationToken);
            oldTeamName = oldTeam.TryGetValue(report.AssignedToTeamId.Value, out var oldName) ? oldName : null;
        }

        if (report.AssignedToUserId != assignedToUserId)
        {
            var fromLabel = oldAssigneeName ?? "Unassigned";
            string toLabel = "Unassigned";
            if (assignedToUserId.HasValue)
            {
                var newUser = await _userService.GetByIdAsync(assignedToUserId.Value, cancellationToken);
                toLabel = newUser?.DisplayName ?? assignedToUserId.Value.ToString();
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
                var names = await _teamService.GetTeamNamesByIdsAsync(
                    [assignedToTeamId.Value], cancellationToken);
                toLabel = names.TryGetValue(assignedToTeamId.Value, out var newName)
                    ? newName
                    : assignedToTeamId.Value.ToString();
            }
            changes.Add($"Team: {fromLabel} → {toLabel}");
            report.AssignedToTeamId = assignedToTeamId;
        }

        if (changes.Count == 0)
            return;

        report.UpdatedAt = _clock.GetCurrentInstant();

        await _repository.SaveTrackedReportAsync(report, cancellationToken);

        // Audit after the business save so a rollback never leaves a ghost audit row.
        var description = $"Feedback {id} assignment changed: {string.Join("; ", changes)}";
        if (actorUserId.HasValue)
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackAssignmentChanged, nameof(FeedbackReport), id,
                description, actorUserId.Value);
        }
        else
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackAssignmentChanged, nameof(FeedbackReport), id,
                description, "API");
        }

        _logger.LogInformation(
            "Feedback {ReportId} assignment updated by {ActorId}: {Changes}",
            id, actorUserId?.ToString() ?? "API", string.Join("; ", changes));
    }

    public Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default) =>
        _repository.GetActionableCountAsync(cancellationToken);

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, int Count)>> GetDistinctReportersAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetReporterCountsAsync(cancellationToken);
        if (rows.Count == 0)
            return [];

        var userIds = rows.Select(r => r.UserId).ToList();
        var users = await _userService.GetByIdsAsync(userIds, cancellationToken);

        return rows
            .Select(r =>
            {
                var name = users.TryGetValue(r.UserId, out var u) ? u.DisplayName : r.UserId.ToString();
                return (r.UserId, name, r.Count);
            })
            .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<FeedbackHandoffResult> SubmitFromAgentAsync(
        Guid userId, Guid conversationId, string summary, string topic,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var reportId = Guid.NewGuid();

        var report = new FeedbackReport
        {
            Id = reportId,
            UserId = userId,
            Category = FeedbackCategory.Question,
            Description = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Topic: {topic}\n\n{summary}"),
            PageUrl = string.Empty,
            Status = FeedbackStatus.Open,
            Source = FeedbackSource.AgentUnresolved,
            AgentConversationId = conversationId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.AddReportAsync(report, cancellationToken);
        _navBadge.Invalidate();

        _logger.LogInformation(
            "Agent feedback handoff {ReportId} submitted for user {UserId}, conversationId {ConversationId}",
            reportId, userId, conversationId);

        return new FeedbackHandoffResult(reportId, $"/Feedback/{reportId}");
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var reports = await _repository.GetForUserExportAsync(userId, ct);

        var shaped = reports.Select(fr => new
        {
            fr.Category,
            fr.Description,
            fr.PageUrl,
            fr.Status,
            CreatedAt = fr.CreatedAt.ToInvariantInstantString(),
            ResolvedAt = fr.ResolvedAt.ToInvariantInstantString(),
            Messages = fr.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                m.Content,
                IsFromUser = m.SenderUserId == userId,
                CreatedAt = m.CreatedAt.ToInvariantInstantString()
            })
        }).ToList();

        return [new UserDataSlice(GdprExportSections.FeedbackReports, shaped)];
    }

    // ==========================================================================
    // Cross-domain nav stitching — populates [Obsolete]-marked nav properties
    // in memory from service calls so controllers/views do not need to change.
    // Design-rules §6b "in-memory join" pattern.
    // ==========================================================================
#pragma warning disable CS0618 // Obsolete cross-domain nav properties populated in-memory

    private async Task StitchCrossDomainNavsAsync(
        IReadOnlyList<FeedbackReport> reports, CancellationToken ct)
    {
        if (reports.Count == 0) return;

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

        var users = userIds.Count == 0
            ? null
            : await _userService.GetByIdsAsync(userIds, ct);
        var teamNames = teamIds.Count == 0
            ? null
            : await _teamService.GetTeamNamesByIdsAsync(teamIds, ct);

        foreach (var r in reports)
        {
            if (users is not null && users.TryGetValue(r.UserId, out var reporter))
                r.User = reporter;

            if (r.ResolvedByUserId.HasValue && users is not null &&
                users.TryGetValue(r.ResolvedByUserId.Value, out var resolver))
                r.ResolvedByUser = resolver;

            if (r.AssignedToUserId.HasValue && users is not null &&
                users.TryGetValue(r.AssignedToUserId.Value, out var assignee))
                r.AssignedToUser = assignee;

            if (r.AssignedToTeamId.HasValue && teamNames is not null &&
                teamNames.TryGetValue(r.AssignedToTeamId.Value, out var teamName))
            {
                r.AssignedToTeam = new Team
                {
                    Id = r.AssignedToTeamId.Value,
                    Name = teamName,
                };
            }

            foreach (var m in r.Messages)
            {
                if (m.SenderUserId.HasValue && users is not null &&
                    users.TryGetValue(m.SenderUserId.Value, out var sender))
                {
                    m.SenderUser = sender;
                }
            }
        }
    }

    private async Task StitchMessageSendersAsync(
        IReadOnlyList<FeedbackMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return;

        var userIds = messages
            .Where(m => m.SenderUserId.HasValue)
            .Select(m => m.SenderUserId!.Value)
            .ToHashSet();

        if (userIds.Count == 0) return;

        var users = await _userService.GetByIdsAsync(userIds, ct);
        foreach (var m in messages)
        {
            if (m.SenderUserId.HasValue && users.TryGetValue(m.SenderUserId.Value, out var sender))
            {
                m.SenderUser = sender;
            }
        }
    }

#pragma warning restore CS0618
}
