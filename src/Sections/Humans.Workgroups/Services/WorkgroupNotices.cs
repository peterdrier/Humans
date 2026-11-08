using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupNotices(
    IUserService users,
    IRoleAssignmentService roles,
    INotificationService notifications,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    ILogger logger) : IApplicationService
{
    private static string PageUrl(WorkgroupInfo w) => $"/Workgroups/{w.Slug}";

    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(WorkgroupsResource));

    // Returns successful dispatch requests for the rhythm audit; Notifications applies inbox preferences.
    internal async Task<int> NotifyAsync(
        IReadOnlyList<Guid> recipientUserIds, NotificationSourceDefinition source, string titleKey,
        WorkgroupInfo w, string? body, CancellationToken ct)
    {
        if (recipientUserIds.Count == 0) return 0;
        IReadOnlyDictionary<Guid, UserInfo> people;
        try
        {
            people = await users.GetUserInfosAsync(recipientUserIds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve workgroup {Source} notification recipients for {WorkgroupId}", source, w.Id);
            return 0;
        }
        // In-app notices are keyed by user id: deliver to the live id the read resolved to.
        var recipients = recipientUserIds.Select(id =>
        {
            var person = people.GetValueOrDefault(id);
            var language = person?.PreferredLanguage;
            return (Id: person?.Id ?? id, Language: language.IsSupportedCultureCode() ? language! : "en");
        }).DistinctBy(person => person.Id);
        var dispatched = 0;
        foreach (var group in recipients.GroupBy(person => person.Language, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(group.Key);
                var title = $"{NoticeResources.GetString(titleKey, culture)}: {w.Name}";
                var ids = group.Select(person => person.Id).ToList();
                await notifications.SendAsync(source, NotificationClass.Informational, NotificationPriority.Normal,
                    title, ids, body,
                    actionUrl: PageUrl(w), sourceKey: w.Id.ToString(), cancellationToken: ct);
                dispatched += ids.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send workgroup {Source} notification in {Language} for {WorkgroupId}",
                    source, group.Key, w.Id);
            }
        }
        return dispatched;
    }

    internal async Task NotifyBoardAsync(
        NotificationSourceDefinition source, string title, WorkgroupInfo w, string body, CancellationToken ct)
    {
        try
        {
            await notifications.SendToRoleAsync(source, NotificationClass.Actionable,
                NotificationPriority.Normal, title, RoleNames.Board, body,
                actionUrl: "/Workgroups/Admin", cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send workgroup {Source} notification to the Board for {WorkgroupId}", source, w.Id);
        }
    }

    /// <summary>Email failures never fail the write: the log and audit are the record, the email a courtesy.</summary>
    internal async Task EmailAsync(
        IReadOnlyList<Guid> recipientUserIds,
        WorkgroupNoticeKind kind,
        WorkgroupInfo w,
        string? detail,
        CancellationToken ct)
    {
        var ids = recipientUserIds.Distinct().ToList();
        if (ids.Count == 0)
            return;

        IReadOnlyDictionary<Guid, UserInfo> infos;
        try
        {
            infos = await users.GetUserInfosAsync(ids, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve recipients for the workgroup {Kind} notice on {WorkgroupSlug}",
                kind, w.Slug);
            return;
        }

        var recipientIds = new HashSet<Guid>();
        foreach (var id in ids)
        {
            infos.TryGetValue(id, out var info);
            var address = info?.Email;
            if (info is null || string.IsNullOrWhiteSpace(address))
            {
                logger.LogWarning(
                    "Skipping workgroup {Kind} email for {UserId} on {WorkgroupId}: no effective email",
                    kind, id, w.Id);
                continue;
            }
            if (!recipientIds.Add(info.Id)) continue;

            var language = info.PreferredLanguage;
            await SendNoticeAsync(new WorkgroupNoticeRequest(
                info.Id, address, info.BurnerName, kind, w.Name, w.Slug, detail,
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
        }
    }

    internal async Task EmailBoardAsync(
        WorkgroupNoticeKind kind, WorkgroupInfo w, string? detail, CancellationToken ct)
    {
        try
        {
            var boardUserIds = await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);
            await EmailAsync(boardUserIds, kind, w, detail, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve the Board for the workgroup {Kind} notice on {WorkgroupSlug}",
                kind, w.Slug);
        }
    }

    private async Task SendNoticeAsync(WorkgroupNoticeRequest request, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(emailFactory.WorkgroupNotice(request), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue the workgroup {Kind} notice for {WorkgroupSlug}",
                request.Kind, request.WorkgroupSlug);
        }
    }

}
