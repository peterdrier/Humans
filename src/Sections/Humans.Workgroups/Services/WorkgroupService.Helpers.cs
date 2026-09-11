using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Helpers;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// The projection off EF entities, the guard clauses, and the one place each crosscut
/// is called from. Kept in its own partial so the rule files above read as rules.
/// </summary>
internal sealed partial class WorkgroupService
{
    // ── Projection ────────────────────────────────────────────────────────

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
        w.DiscordChannelUrl,
        w.AppliedByUserId,
        w.AppliedAt,
        w.RegisteredAt,
        w.EndedAt,
        w.DormantSince,
        w.Members
            .OrderBy(m => m.Role == WorkgroupMemberRole.Coordinator ? 0 : 1)
            .ThenBy(m => m.JoinedAt)
            .Select(m => new WorkgroupMemberInfo(m.Id, m.UserId, m.Role, m.JoinedAt, m.LeftAt))
            .ToList(),
        // Soft-deleted meetings are gone as far as anything outside the repository is
        // concerned; the log keeps the history of what was scheduled.
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
            .ToList());

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

    // ── Guards ────────────────────────────────────────────────────────────

    private async Task<Workgroup> RequireAsync(Guid workgroupId, CancellationToken ct) =>
        await repository.GetWorkgroupAsync(workgroupId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);

    /// <summary>Member mutations are frozen before registration and once the group ends.</summary>
    private static void RequireAcceptsMemberWork(Workgroup w)
    {
        if (w.Status != WorkgroupStatus.Active)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.Frozen);
    }

    private static void RequireStatus(Workgroup w, params WorkgroupStatus[] allowed)
    {
        if (!allowed.Contains(w.Status))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.WrongStatus);
    }

    /// <summary>The four kinds a human writes; everything else is the section's own record.</summary>
    private static void RequireMemberKind(WorkgroupLogKind kind)
    {
        if (kind is not (WorkgroupLogKind.Update or WorkgroupLogKind.Disclosure
            or WorkgroupLogKind.StatusRequested or WorkgroupLogKind.Note))
        {
            throw new WorkgroupRuleException(WorkgroupErrorKeys.SystemLogEntry);
        }
    }

    private static void RequireReasons(string? reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.ReasonsRequired);
    }

    private static void ValidateApplication(WorkgroupApplication application) =>
        ValidateRegisterFields(application.Name, application.Purpose, application.Deliverable);

    private static void ValidateRegisterFields(string name, string purpose, string deliverable)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NameRequired);
        if (string.IsNullOrWhiteSpace(purpose))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.PurposeRequired);
        if (string.IsNullOrWhiteSpace(deliverable))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.DeliverableRequired);
    }

    private static void ValidateMeeting(WorkgroupMeetingSave save)
    {
        if (string.IsNullOrWhiteSpace(save.Title))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NameRequired);
        if (save.EndUtc <= save.StartUtc)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.WindowInvalid);
    }

    // ── Field application ─────────────────────────────────────────────────

    private static void ApplyFields(
        Workgroup w,
        string name,
        string purpose,
        string deliverable,
        WorkgroupDeliverableKind kind,
        WorkgroupAudience audience,
        LocalDate? targetDate,
        string? discordChannelUrl)
    {
        w.Name = name.Trim();
        w.Purpose = purpose.Trim();
        w.Deliverable = deliverable.Trim();
        w.DeliverableKind = kind;
        w.Audience = audience;
        w.TargetDate = targetDate;
        w.DiscordChannelUrl = Trimmed(discordChannelUrl);
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
        Guid workgroupId, Guid userId, WorkgroupMemberRole role, Instant now) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            UserId = userId,
            Role = role,
            JoinedAt = now
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
    /// A kebab slug from the name, suffixed until it is free. Reserved segments
    /// (<c>/Workgroups/Apply</c>, <c>/Workgroups/Admin</c>) are taken by routes, so a group
    /// called "Apply" gets <c>apply-2</c> rather than shadowing the form.
    /// </summary>
    private async Task<string> ReserveSlugAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (string.IsNullOrEmpty(baseSlug))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NameRequired);

        var candidate = baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            if (!ReservedSlugs.Contains(candidate, StringComparer.Ordinal)
                && !await repository.SlugTakenAsync(candidate, exceptId, ct))
            {
                return candidate;
            }

            // A hundred groups sharing one name is a data problem, not a naming problem.
            if (suffix > 100)
                throw new WorkgroupRuleException(WorkgroupErrorKeys.NameTaken);

            candidate = $"{baseSlug}-{suffix}";
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Names ─────────────────────────────────────────────────────────────

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

    // ── Notifications and email ───────────────────────────────────────────

    private static string PageUrl(WorkgroupInfo w) => $"/Workgroups/{w.Slug}";

    private async Task NotifyAsync(
        IReadOnlyList<Guid> recipientUserIds,
        NotificationSource source,
        string title,
        WorkgroupInfo w,
        string body,
        CancellationToken ct)
    {
        if (recipientUserIds.Count == 0)
            return;

        await notifications.SendAsync(source, NotificationClass.Informational,
            NotificationPriority.Normal, title, recipientUserIds, body,
            actionUrl: PageUrl(w), sourceKey: w.Id.ToString(), cancellationToken: ct);
    }

    private Task NotifyBoardAsync(
        NotificationSource source, string title, WorkgroupInfo w, string body, CancellationToken ct) =>
        notifications.SendToRoleAsync(source, NotificationClass.Actionable,
            NotificationPriority.Normal, title, RoleNames.Board, body,
            actionUrl: "/Workgroups/Admin", cancellationToken: ct);

    /// <summary>
    /// One notice per recipient, addressed by name. Email failures never fail the write:
    /// the log entry and the audit trail are the record, the email is a courtesy.
    /// </summary>
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

        var addresses = await userEmails.GetNotificationTargetEmailsAsync(ids, ct);
        var infos = await users.GetUserInfosAsync(ids, ct);

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
            await SendNoticeAsync(new WorkgroupNoticeRequest(
                address, info?.BurnerName, kind, w.Name, w.Slug, detail, info?.PreferredLanguage), ct);
        }
    }

    private async Task EmailBoardAsync(
        WorkgroupNoticeKind kind, WorkgroupInfo w, string? detail, CancellationToken ct)
    {
        var boardUserIds = await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);
        await EmailAsync(boardUserIds, kind, w, detail, ct);
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

    // ── Audit ─────────────────────────────────────────────────────────────

    private async Task AuditAsync(
        AuditAction action,
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

    /// <summary>The daily job's actions are the section's, not a human's — they audit by job name.</summary>
    private async Task AuditJobAsync(AuditAction action, Workgroup w, string description)
    {
        await auditLog.LogAsync(action, AuditEntityTypes.Workgroup, w.Id, description,
            WorkgroupRhythmJobName);
    }

    /// <summary>Audit actor name for the daily rhythm pass.</summary>
    internal const string WorkgroupRhythmJobName = "WorkgroupRhythmJob";

    // ── Drive ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks GoogleIntegration to reconcile the group's folder against the access source.
    /// A group with no folder yet (Applied, Referred, Refused) has nothing to reconcile.
    /// Failures are logged, never surfaced: the daily reconciliation covers the drift.
    /// </summary>
    private async Task RequestDriveSyncAsync(Workgroup w, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(w.DriveFolderId))
            return;

        try
        {
            await googleSync.RequestSyncAsync(w.DriveFolderId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to request a Drive access sync for workgroup {WorkgroupId}", w.Id);
        }
    }

    // ── Shared transitions ────────────────────────────────────────────────

    /// <summary>Any sign of life clears the dormancy inquiry flag (§13).</summary>
    private async Task ClearDormancyFlagAsync(Workgroup w, Instant now, CancellationToken ct)
    {
        if (w.DormantSince is null)
            return;

        w.DormantSince = null;
        w.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(w, ct);
    }

    /// <summary>
    /// The one place a group ends, whether a member marked it done or the Secretary closed
    /// it: Dormant with a reason, the log entry, the audit entry, the coordinators told, and
    /// the Drive folder dropped to read-only through the access source.
    /// </summary>
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
        w.DormantSince = null;
        w.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(w, ct);

        await AddSystemEntryAsync(w, WorkgroupLogKind.Ended, now,
            Trimmed(reasons) ?? reason.ToString(), ct,
            authorUserId: reason == WorkgroupDormantReason.Quiet ? null : actorUserId);

        await AuditAsync(AuditAction.WorkgroupClosed, w, $"Group ended ({reason})", actorUserId);

        var info = ToInfo(w);
        var coordinators = info.CoordinatorUserIds();
        await NotifyAsync(coordinators, NotificationSource.WorkgroupRegistrationDecided,
            $"Working group ended: {w.Name}", info,
            Trimmed(reasons) ?? $"The group is now dormant ({reason}).", ct);
        await EmailAsync(coordinators, WorkgroupNoticeKind.Ended, info, Trimmed(reasons), ct);

        // Dormant means read-only for the folder: the source now returns Viewer.
        await RequestDriveSyncAsync(w, ct);
    }
}
