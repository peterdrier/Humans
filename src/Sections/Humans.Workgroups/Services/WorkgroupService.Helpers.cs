using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>Projection, guards and crosscut calls, kept apart so the rule files read as rules.</summary>
internal sealed partial class WorkgroupService
{
    private static WorkgroupInfo ToInfo(Workgroup w) => new(
        w.Id,
        w.Slug,
        w.Name,
        w.Purpose,
        w.Deliverable,
        w.DeliverableKind,
        w.Audience,
        w.TargetDate,
        w.Status,
        w.DormantReason,
        w.Reasons,
        w.DriveFolderId,
        SafeDiscordLink(w.DiscordChannelUrl),
        w.AppliedByUserId,
        w.AppliedAt,
        w.RegisteredAt,
        w.EndedAt,
        w.Members
            .OrderBy(m => m.Role == WorkgroupMemberRole.Coordinator ? 0 : 1)
            .ThenBy(m => m.JoinedAt)
            .Select(m => new WorkgroupMemberInfo(m.Id, m.UserId, m.Role, m.JoinedAt, m.LeftAt, m.IsPending == true))
            .ToList(),
        // Soft-deleted meetings are gone outside the repository; the log keeps the history.
        w.Meetings
            .Where(m => m.DeletedAt is null)
            .OrderBy(m => m.StartUtc)
            .Select(m => new WorkgroupMeetingInfo(
                m.Id, m.Title, m.StartUtc, m.EndUtc, m.Location, m.LocationUrl, m.IsPublic,
                m.Minutes, m.CreatedByUserId, m.CreatedAt, m.UpdatedAt))
            .ToList(),
        w.LogEntries
            .OrderByDescending(e => e.OccurredOn)
            .ThenByDescending(e => e.CreatedAt)
            .Select(e => new WorkgroupLogEntryInfo(
                e.Id, e.Kind, e.OccurredOn, e.Title, e.Body, e.AuthorUserId, e.DocumentId,
                e.SurveyId, e.CreatedAt, e.UpdatedAt))
            .ToList(),
        w.Documents
            .OrderByDescending(d => d.CreatedAt)
            .Select(ToInfo)
            .ToList(),
        w.BudgetAmount,
        w.HoldedAccountNumber,
        w.HoldedAccountId,
        w.IsSensitive == true);

    private static WorkgroupDocumentInfo ToInfo(WorkgroupDocument d) => new(
        d.Id,
        d.WorkgroupId,
        d.Title,
        d.Kind,
        d.Body,
        d.Status,
        d.CommentCategories.ToList(),
        d.CommentsOpenAt,
        d.CommentsCloseAt,
        d.DeliveredAt,
        d.Disposition,
        d.DispositionNote,
        d.DispositionAt,
        d.DispositionByUserId,
        d.CreatedByUserId,
        d.UpdatedByUserId,
        d.CreatedAt,
        d.UpdatedAt,
        d.Comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => new WorkgroupCommentInfo(
                c.Id, c.DocumentId, c.Category, c.AuthorUserId, c.Body, c.CreatedAt,
                c.Disposition, c.Response, c.RespondedByUserId, c.RespondedAt, c.HiddenAt,
                c.HiddenByUserId, c.HiddenReason))
            .ToList());

    private async Task<WorkgroupMutationResult<Workgroup>> RequireAsync(Guid workgroupId, CancellationToken ct)
    {
        var workgroup = await repository.GetWorkgroupAsync(workgroupId, ct);
        return workgroup is null
            ? new(Refusal: new(WorkgroupErrorKeys.NotFound))
            : new(workgroup);
    }

    /// <summary>The group, refused unless it exists and accepts member work.</summary>
    private async Task<WorkgroupMutationResult<Workgroup>> RequireActiveAsync(Guid workgroupId, CancellationToken ct)
    {
        var result = await RequireAsync(workgroupId, ct);
        if (result.Refusal is null && RequireAcceptsMemberWork(result.Value!) is { } stateRefusal)
            return new(Refusal: stateRefusal);
        return result;
    }

    private async Task<WorkgroupMutationResult<WorkgroupMeeting>> RequireLiveMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await repository.GetMeetingAsync(meetingId, ct);
        return meeting is { DeletedAt: null }
            ? new(meeting)
            : new(Refusal: new(WorkgroupErrorKeys.NotFound));
    }

    /// <summary>Member mutations are frozen before registration and once the group ends.</summary>
    private static WorkgroupRefusal? RequireAcceptsMemberWork(Workgroup w)
    {
        if (w.Status != WorkgroupStatus.Active)
            return new(WorkgroupErrorKeys.Frozen);
        return null;
    }

    private static WorkgroupRefusal? RequireCoordinator(Workgroup w, Guid actorUserId, bool asAdmin)
    {
        if (asAdmin) return null;
        if (!w.Members.Any(m => m.UserId == actorUserId && m.LeftAt is null && m.IsPending != true
                && m.Role == WorkgroupMemberRole.Coordinator))
        {
            return new(WorkgroupErrorKeys.NotACoordinator);
        }
        return null;
    }

    private static WorkgroupRefusal? RequireStatus(Workgroup w, params WorkgroupStatus[] allowed)
    {
        if (!allowed.Contains(w.Status))
            return new(WorkgroupErrorKeys.WrongStatus);
        return null;
    }

    private static WorkgroupRefusal? RequireMemberKind(WorkgroupLogKind kind)
    {
        if (kind is not (WorkgroupLogKind.Update or WorkgroupLogKind.Disclosure
            or WorkgroupLogKind.StatusRequested or WorkgroupLogKind.Note))
        {
            return new(WorkgroupErrorKeys.SystemLogEntry);
        }
        return null;
    }

    private static WorkgroupRefusal? RequireReasons(string? reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
            return new(WorkgroupErrorKeys.ReasonsRequired);
        if (reasons.Trim().Length > 4000)
            return new(WorkgroupErrorKeys.TextTooLong, 4000);
        return null;
    }

    private async Task<WorkgroupMutationResult<WorkgroupApplication>> ValidateApplicationAsync(
        WorkgroupApplication application, CancellationToken ct)
    {
        if (ValidateRegisterFields(application.Name, application.Purpose, application.Deliverable, application.DiscordChannelUrl) is { } refusal)
            return new(Refusal: refusal);
        if (application.SecondCoordinatorUserId is not { } second)
            return new(application);
        var coordinator = await ResolveMemberTargetAsync(second, ct);
        return coordinator is null
            ? new(Refusal: new(WorkgroupErrorKeys.NotFound))
            : new(application with { SecondCoordinatorUserId = coordinator.Value });
    }

    private async Task<Guid?> ResolveMemberTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        return user is null || user.IsTombstone ? null : user.Id;
    }

    private static WorkgroupRefusal? ValidateRegisterFields(string name, string purpose, string deliverable, string? discordChannelUrl)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new(WorkgroupErrorKeys.NameRequired);
        if (string.IsNullOrWhiteSpace(purpose))
            return new(WorkgroupErrorKeys.PurposeRequired);
        if (string.IsNullOrWhiteSpace(deliverable))
            return new(WorkgroupErrorKeys.DeliverableRequired);
        if (!string.IsNullOrWhiteSpace(discordChannelUrl) && SafeDiscordLink(discordChannelUrl) is null)
            return new(WorkgroupErrorKeys.DiscordUrlInvalid);
        return null;
    }

    // Also guards stored legacy links when projecting them into an href.
    private static string? SafeDiscordLink(string? url)
    {
        var trimmed = Trimmed(url);
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? trimmed : null;
    }

    private static WorkgroupRefusal? ValidateMeeting(WorkgroupMeetingSave save)
    {
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(WorkgroupErrorKeys.NameRequired);
        if (save.EndUtc <= save.StartUtc)
            return new(WorkgroupErrorKeys.WindowInvalid);
        if (save.LocationUrl?.Trim().Length > 2000)
            return new(WorkgroupErrorKeys.TextTooLong, 2000);
        return null;
    }

    private static void ApplyFields(
        Workgroup w,
        string name,
        string purpose,
        string deliverable,
        WorkgroupDeliverableKind kind,
        WorkgroupAudience audience,
        LocalDate? targetDate,
        string? discordChannelUrl,
        bool isSensitive)
    {
        w.Name = name.Trim();
        w.Purpose = purpose.Trim();
        w.Deliverable = deliverable.Trim();
        w.DeliverableKind = kind;
        w.Audience = audience;
        w.TargetDate = targetDate;
        w.DiscordChannelUrl = Trimmed(discordChannelUrl);
        w.IsSensitive = isSensitive ? true : null;
    }

    private static void ApplyMeetingFields(WorkgroupMeeting meeting, WorkgroupMeetingSave save)
    {
        meeting.Title = save.Title.Trim();
        meeting.StartUtc = save.StartUtc;
        meeting.EndUtc = save.EndUtc;
        meeting.Location = Trimmed(save.Location);
        meeting.LocationUrl = Trimmed(save.LocationUrl);
        meeting.IsPublic = save.IsPublic;
        meeting.Minutes = Trimmed(save.Minutes);
    }

    private static WorkgroupMember NewMember(
        Guid workgroupId, Guid userId, WorkgroupMemberRole role, Instant now, bool isPending = false) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            UserId = userId,
            Role = role,
            JoinedAt = now,
            IsPending = isPending ? true : null
        };

    private static WorkgroupLogEntry SystemEntry(
        Guid workgroupId,
        WorkgroupLogKind kind,
        Instant now,
        string? body,
        Guid? authorUserId = null,
        Guid? documentId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Kind = kind,
            OccurredOn = now.InUtc().Date,
            Body = Trimmed(body),
            AuthorUserId = authorUserId,
            DocumentId = documentId,
            CreatedAt = now,
            UpdatedAt = now
        };

    private Task AddSystemEntryAsync(
        Workgroup w,
        WorkgroupLogKind kind,
        Instant now,
        string? body,
        CancellationToken ct,
        Guid? authorUserId = null,
        Guid? documentId = null) =>
        repository.AddLogEntryAsync(SystemEntry(w.Id, kind, now, body, authorUserId, documentId), ct);

    /// <summary>
    /// Suffixed until free. Reserved route segments count as taken, so a group called "Apply" gets <c>apply-2</c>
    /// rather than shadowing the form.
    /// </summary>
    private async Task<WorkgroupMutationResult<string>> ReserveSlugAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (string.IsNullOrEmpty(baseSlug))
            baseSlug = "workgroup";

        // Name allows 200 characters but Slug holds 100, with room for a "-100" suffix: trim rather than fail on insert.
        if (baseSlug.Length > SlugBudget)
            baseSlug = baseSlug[..SlugBudget].TrimEnd('-');

        var candidate = baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            if (!ReservedSlugs.Contains(candidate, StringComparer.Ordinal)
                && !await repository.SlugTakenAsync(candidate, exceptId, ct))
            {
                return new(candidate);
            }

            // A hundred groups sharing one name is a data problem, not a naming problem.
            if (suffix > 100)
                return new(Refusal: new(WorkgroupErrorKeys.NameTaken));

            candidate = $"{baseSlug}-{suffix}";
        }
    }

    private const int SlugBudget = 96;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string> NameOfAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info?.BurnerName ?? "A member";
    }

    private async Task<IReadOnlyList<string>> NamesOfAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var infos = await users.GetUserInfosAsync(userIds, ct);
        return userIds
            .Select(id => infos.TryGetValue(id, out var info) ? info.BurnerName : "A member")
            .ToList();
    }

    private static string PageUrl(WorkgroupInfo w) => $"/Workgroups/{w.Slug}";

    private static readonly System.Resources.ResourceManager NoticeResources = new(typeof(WorkgroupsResource));

    // Returns successful dispatch requests for the rhythm audit; Notifications applies inbox preferences.
    private async Task<int> NotifyAsync(
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

    private async Task NotifyBoardAsync(
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
    private async Task EmailAsync(
        IReadOnlyList<Guid> recipientUserIds,
        WorkgroupNoticeKind kind,
        WorkgroupInfo w,
        string? detail,
        CancellationToken ct)
    {
        var ids = recipientUserIds.Distinct().ToList();
        if (ids.Count == 0)
            return;

        IReadOnlyDictionary<Guid, string> addresses;
        IReadOnlyDictionary<Guid, UserInfo> infos;
        try
        {
            addresses = await userEmails.GetNotificationTargetEmailsAsync(ids, ct);
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

        foreach (var id in ids)
        {
            if (!addresses.TryGetValue(id, out var address) || string.IsNullOrWhiteSpace(address))
            {
                logger.LogWarning(
                    "Skipping workgroup {Kind} email for {UserId} on {WorkgroupId}: no effective email",
                    kind, id, w.Id);
                continue;
            }

            infos.TryGetValue(id, out var info);
            var language = info?.PreferredLanguage;
            await SendNoticeAsync(new WorkgroupNoticeRequest(
                id, address, info?.BurnerName, kind, w.Name, w.Slug, detail,
                language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
        }
    }

    private async Task EmailBoardAsync(
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

    private async Task AuditAsync(
        AuditActionDefinition action,
        Workgroup w,
        string description,
        Guid actorUserId,
        string entityType = AuditEntityTypes.Workgroup,
        Guid? entityId = null)
    {
        var id = entityId ?? w.Id;
        await auditLog.LogAsync(action, entityType, id, description, actorUserId,
            relatedEntityId: id == w.Id ? null : w.Id,
            relatedEntityType: id == w.Id ? null : AuditEntityTypes.Workgroup);
    }

    private async Task AuditJobAsync(AuditActionDefinition action, Workgroup w, string description)
    {
        await auditLog.LogAsync(action, AuditEntityTypes.Workgroup, w.Id, description,
            WorkgroupRhythmJobName);
    }

    internal const string WorkgroupRhythmJobName = "WorkgroupRhythmJob";

    /// <summary>Failures are logged, never surfaced: the daily reconciliation covers the drift.</summary>
    private async Task RequestDriveSyncAsync(Workgroup w, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(w.DriveFolderId))
            return;

        try
        {
            await googleSyncWrite.RequestSyncAsync(w.DriveFolderId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to request a Drive access sync for workgroup {WorkgroupId}", w.Id);
        }
    }

    /// <summary>
    /// Failures are logged, never surfaced: the lifecycle step is the Board's decision and doesn't wait on Holded.
    /// </summary>
    private async Task SetAccountActiveAsync(Workgroup w, bool isActive, CancellationToken ct)
    {
        if (w.HoldedAccountNumber is not { } accountNum) return;
        try
        {
            await finance.SetExpenseAccountActiveAsync(accountNum, isActive, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not set Holded account {AccountNum} active={IsActive} for workgroup {WorkgroupId}",
                accountNum, isActive, w.Id);
        }
    }

    /// <summary>A group that stops being Active has nobody left to decide its join requests. Nothing is logged.</summary>
    private async Task ClosePendingRequestsAsync(Workgroup w, Instant now, CancellationToken ct)
    {
        var pending = w.Members.Where(m => m.LeftAt is null && m.IsPending == true).ToList();
        if (pending.Count == 0) return;
        foreach (var m in pending) m.LeftAt = now;
        await repository.UpdateMembersAsync(pending, ct);
    }

    /// <summary>The one place a group ends, whether a member marked it done or the Secretary closed it.</summary>
    private async Task EndAsync(
        Workgroup w,
        Guid actorUserId,
        WorkgroupDormantReason reason,
        string? reasons,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        w.Status = WorkgroupStatus.Dormant;
        w.DormantReason = reason;
        w.Reasons = Trimmed(reasons) ?? w.Reasons;
        w.EndedAt = now;
        w.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(w, ct);
        ct = CancellationToken.None;
        await ClosePendingRequestsAsync(w, now, ct);

        await AddSystemEntryAsync(w, WorkgroupLogKind.Ended, now,
            Trimmed(reasons) ?? reason.ToString(), ct,
            authorUserId: reason == WorkgroupDormantReason.Quiet ? null : actorUserId);

        await AuditAsync(WorkgroupsAuditActions.WorkgroupClosed, w, $"Group ended ({reason})", actorUserId);

        var info = ToInfo(w);
        var coordinators = info.CoordinatorUserIds();
        await NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_Ended", info, Trimmed(reasons), ct);
        await EmailAsync(coordinators, WorkgroupNoticeKind.Ended, info, Trimmed(reasons), ct);

        // Dormant: the access source now returns Viewer.
        await RequestDriveSyncAsync(w, ct);
        await SetAccountActiveAsync(w, isActive: false, ct);
    }
}
