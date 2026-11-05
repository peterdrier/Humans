using Humans.AuditLog.Contracts;
using Humans.Calendar.Contracts;
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
            .Select(m => new WorkgroupMemberInfo(m.Id, m.UserId, m.Role, m.JoinedAt, m.LeftAt, m.IsPending == true))
            .ToList(),
        // Soft-deleted meetings are gone outside the repository; the log keeps the history.
        w.Meetings
            .Where(m => m.DeletedAt is null)
            .Select(m => new WorkgroupMeetingInfo(
                m.Id, m.Title, m.StartUtc, m.EndUtc, m.Location, m.LocationUrl, m.IsPublic,
                m.Minutes, m.CreatedByUserId, m.CreatedAt, m.UpdatedAt, m.Description,
                m.RecurrenceRule, m.RecurrenceTimezone))
            .ToList(),
        w.LogEntries
            .Select(e => new WorkgroupLogEntryInfo(
                e.Id, e.Kind, e.OccurredOn, e.Title, e.Body, e.AuthorUserId, e.DocumentId,
                e.SurveyId, e.CreatedAt, e.UpdatedAt, e.MeetingId))
            .ToList(),
        w.Documents
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

    /// <summary>
    /// The meeting id the entry keeps, or a refusal. Only meeting notes keep a link, and a new link must name one of
    /// the group's live meetings; an unchanged link survives its meeting's deletion, so old notes stay editable.
    /// </summary>
    private async Task<WorkgroupMutationResult<Guid?>> ResolveMeetingLinkAsync(
        Guid workgroupId, WorkgroupLogEntrySave save, CancellationToken ct, Guid? currentMeetingId = null)
    {
        if (save.Kind != WorkgroupLogKind.MeetingNotes || save.MeetingId is not { } meetingId) return new();
        if (meetingId == currentMeetingId) return new(meetingId);
        var meeting = await RequireLiveMeetingAsync(meetingId, ct);
        return meeting.Value?.WorkgroupId == workgroupId ? new(meetingId) : new(Refusal: new(WorkgroupErrorKeys.NotFound));
    }

    /// <summary>Member mutations are frozen before registration and once the group ends.</summary>
    private static WorkgroupRefusal? RequireAcceptsMemberWork(Workgroup w)
    {
        if (w.Status != WorkgroupStatus.Active)
            return new(WorkgroupErrorKeys.Frozen);
        return null;
    }

    private async Task<WorkgroupMutationResult<Workgroup>> RequireMembershipManagerAsync(
        Guid workgroupId, Guid actorUserId, bool asAdmin, CancellationToken ct)
    {
        var result = await RequireActiveAsync(workgroupId, ct);
        if (result.Refusal is not null) return result;
        if (!asAdmin && !result.Value!.Members.Any(m => m.UserId == actorUserId && m.LeftAt is null && m.IsPending != true
                && m.Role == WorkgroupMemberRole.Coordinator))
        {
            return new(Refusal: new(WorkgroupErrorKeys.NotACoordinator));
        }
        return result;
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
            or WorkgroupLogKind.StatusRequested or WorkgroupLogKind.Note or WorkgroupLogKind.MeetingNotes))
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
        if (save.Description?.Trim().Length > 4000)
            return new(WorkgroupErrorKeys.TextTooLong, 4000);
        // The rule and its zone travel together: a rule without a zone can't say which 19:00 it repeats.
        var hasRule = !string.IsNullOrWhiteSpace(save.RecurrenceRule);
        if (hasRule != !string.IsNullOrWhiteSpace(save.RecurrenceTimezone)
            || !CalendarRecurrence.IsValidRule(save.RecurrenceRule)
            || (hasRule && (CalendarEventForm.TryResolveZone(save.RecurrenceTimezone) is null || RepeatsWithinADay(save.RecurrenceRule!))))
            return new(WorkgroupErrorKeys.RecurrenceInvalid);
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

    // The group page and the daily job expand a series from its start, so a sub-daily rule would
    // build an occurrence per second or minute; nobody meets that often.
    private static bool RepeatsWithinADay(string rule) =>
        rule.Split(';').Any(part =>
        {
            var equals = part.IndexOf('=');
            return equals >= 0
                && string.Equals(part[..equals].Trim(), "FREQ", StringComparison.OrdinalIgnoreCase)
                && part[(equals + 1)..].Trim().ToUpperInvariant() is "SECONDLY" or "MINUTELY" or "HOURLY";
        });

    private static void ApplyMeetingFields(WorkgroupMeeting meeting, WorkgroupMeetingSave save)
    {
        meeting.Title = save.Title.Trim();
        meeting.StartUtc = save.StartUtc;
        meeting.EndUtc = save.EndUtc;
        meeting.Description = Trimmed(save.Description);
        meeting.Location = Trimmed(save.Location);
        meeting.LocationUrl = Trimmed(save.LocationUrl);
        meeting.IsPublic = save.IsPublic;
        meeting.RecurrenceRule = Trimmed(save.RecurrenceRule);
        meeting.RecurrenceTimezone = meeting.RecurrenceRule is null ? null : save.RecurrenceTimezone!.Trim();
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
    private static IReadOnlyList<WorkgroupMember> ClosePendingRequests(Workgroup w, Instant now)
    {
        var pending = w.Members.Where(m => m.LeftAt is null && m.IsPending == true).ToList();
        foreach (var m in pending) m.LeftAt = now;
        return pending;
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
        await repository.UpdateWorkgroupAsync(w, ct,
            [SystemEntry(w.Id, WorkgroupLogKind.Ended, now, Trimmed(reasons) ?? reason.ToString(),
                authorUserId: reason == WorkgroupDormantReason.Quiet ? null : actorUserId)],
            ClosePendingRequests(w, now));
        ct = CancellationToken.None;

        await AuditAsync(WorkgroupsAuditActions.WorkgroupClosed, w, $"Group ended ({reason})", actorUserId);

        var info = ToInfo(w);
        var coordinators = info.CoordinatorUserIds();
        await notices.NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_Ended", info, Trimmed(reasons), ct);
        await notices.EmailAsync(coordinators, WorkgroupNoticeKind.Ended, info, Trimmed(reasons), ct);

        // Dormant: the access source now returns Viewer.
        await RequestDriveSyncAsync(w, ct);
        await SetAccountActiveAsync(w, isActive: false, ct);
    }
}
