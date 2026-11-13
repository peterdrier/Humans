using System.Globalization;
using System.Resources;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Issues.Services;

internal sealed class IssueNotices(
    IUserService users,
    IRoleAssignmentService roles,
    IEmailService email,
    IssuesEmails emailMessages,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationInbox,
    IssueSectionRouting routing,
    ILogger logger) : IApplicationService
{
    private static readonly ResourceManager NoticeResources = new(typeof(IssuesResource));

    internal async Task DispatchCommentNotificationsAsync(
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

        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueComment,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                recipients.ToList(),
                culture => (Notice(culture, "Issue_Notice_CommentTitle", issue.Title), comment.Content),
                link, comment.SenderUserId, ct);
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
            var to = reporter?.Email;

            if (reporter is not null &&
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Issue comment {CommentId} saved but its reporter email failed for issue {IssueId}",
                comment.Id, issue.Id);
        }
    }

    internal async Task DispatchStatusChangedNotificationAsync(
        Issue issue, IssueStatus oldStatus, IssueStatus newStatus,
        Guid? actorUserId, CancellationToken ct)
    {
        // Both must differ from the actor.
        var recipients = new HashSet<Guid>();
        if (issue.ReporterUserId != actorUserId) recipients.Add(issue.ReporterUserId);
        if (issue.AssigneeUserId is { } aid && aid != actorUserId) recipients.Add(aid);
        try
        {
            await SendLocalizedNotificationAsync(
                IssuesNotificationSources.IssueStatusChanged,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                recipients.ToList(),
                culture => (
                    Notice(culture, "Issue_Notice_StatusChangedTitle", issue.Title),
                    Notice(culture, "Issue_Notice_StatusChangedBody",
                        Notice(culture, $"Enum_IssueStatus_{oldStatus}"),
                        Notice(culture, $"Enum_IssueStatus_{newStatus}"))),
                $"/Issues/{issue.Id}", actorUserId, ct);
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
        {
            try
            {
                ids.UnionWith(await roles.GetActiveUserIdsInRoleAsync(role, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve issue notice recipients for queue {Section} role {Role}",
                    section ?? "(Admin)", role);
            }
        }
        return ids;
    }

    internal async Task DispatchSubmittedNotificationAsync(Issue issue, CancellationToken ct)
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
                $"/Issues/{issue.Id}", issue.ReporterUserId, ct, issue.Id.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch IssueSubmitted notification for issue {IssueId}",
                issue.Id);
        }
    }

    internal async Task ResolveSubmittedNotificationsAsync(
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

    internal async Task DispatchAssignedNotificationAsync(
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
                $"/Issues/{issue.Id}", actorUserId, ct);
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
        Guid? excludeUserId,
        CancellationToken ct,
        string? sourceKey = null)
    {
        if (recipients.Count == 0) return;
        var lookupIds = recipients.ToHashSet();
        if (excludeUserId is { } actorId) lookupIds.Add(actorId);
        var people = await users.GetUserInfosAsync(lookupIds.ToList(), ct);
        var excludedId = excludeUserId is { } excluded
            ? people.GetValueOrDefault(excluded)?.Id ?? excluded
            : (Guid?)null;
        var targets = recipients.Select(id =>
            {
                var info = people.GetValueOrDefault(id);
                return (Id: info?.Id ?? id, Language: info?.PreferredLanguage);
            })
            .Where(target => target.Id != excludedId)
            .DistinctBy(target => target.Id);
        foreach (var group in targets.GroupBy(
                     target => target.Language.IsSupportedCultureCode() ? target.Language! : "en",
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(group.Key);
                var (title, body) = content(culture);
                await notifications.SendAsync(
                    source, notificationClass, priority, title, group.Select(target => target.Id).ToList(), body,
                    actionUrl: actionUrl,
                    actionLabel: Notice(culture, "Issue_Notice_Open"),
                    sourceKey: sourceKey,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch {Source} issue notices for language {Language}", source, group.Key);
            }
        }
    }
}
