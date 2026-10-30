using System.Globalization;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Surveys.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService(
    IWorkgroupRepository repository,
    IUserService users,
    ISurveyAnalysisRead surveys,
    IUserEmailService userEmails,
    IRoleAssignmentService roles,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    IGoogleSyncWrite googleSyncWrite,
    IHoldedFinanceWrite finance,
    IHoldedClient holded,
    INotificationService notifications,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<WorkgroupService> logger) : IWorkgroupService
{
    private static readonly string[] ReservedSlugs = ["apply", "admin"];

    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        var graph = await repository.GetGraphAsync(ct);
        return graph.Workgroups.Select(ToInfo).ToList();
    }

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.Ordinal));

    public async Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var workgroup = await repository.GetWorkgroupAsync(id, ct);
        return workgroup is null ? null : ToInfo(workgroup);
    }

    public async Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default)
    {
        var register = await GetRegisterAsync(ct);
        return register.Where(w => w.IsMember(userId)).ToList();
    }

    public async Task<WorkgroupMutationResult<Guid>> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(application);
        var validation = await ValidateApplicationAsync(application, ct);
        if (validation.Refusal is { } applicationRefusal) return new(Refusal: applicationRefusal);
        application = validation.Value!;

        var now = clock.GetCurrentInstant();
        var slugResult = await ReserveSlugAsync(application.Name, null, ct);
        if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
        var slug = slugResult.Value!;

        var workgroup = new Workgroup
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Status = WorkgroupStatus.Applied,
            AppliedByUserId = actorUserId,
            AppliedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyFields(workgroup, application.Name, application.Purpose, application.Deliverable,
            application.DeliverableKind, application.Audience, application.TargetDate,
            application.DiscordChannelUrl, application.IsSensitive);

        workgroup.Members.Add(NewMember(workgroup.Id, actorUserId, WorkgroupMemberRole.Coordinator, now));
        if (application.SecondCoordinatorUserId is { } second && second != actorUserId)
            workgroup.Members.Add(NewMember(workgroup.Id, second, WorkgroupMemberRole.Coordinator, now));

        workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.Applied, now,
            body: null));

        await repository.AddWorkgroupAsync(workgroup, ct);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Working group applied: {workgroup.Name}", info,
            "A new working group is waiting on the Secretary.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Applied, info, detail: null, ct);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var existing = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (existing is not null)
            return new(Refusal: new(existing.IsPending == true ? WorkgroupErrorKeys.MembershipPending : WorkgroupErrorKeys.AlreadyAMember));

        var now = clock.GetCurrentInstant();
        await repository.AddMemberAsync(
            NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, now, isPending: true), ct);
        // Committed: the notice must survive a closed tab. It names nobody: the requester's name lives only on the
        // pending row, which erasure deletes, never in an inbox it can't reach.
        ct = CancellationToken.None;
        var info = ToInfo(workgroup);
        await NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupMembershipRequested,
            "Workgroups_Notice_MembershipRequested", info, body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireCoordinator(workgroup, actorUserId, asAdmin) is { } roleRefusal) return new(Refusal: roleRefusal);

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        return await AdmitAsync(workgroup, pending, actorUserId, WorkgroupsAuditActions.WorkgroupMembershipApproved,
            "Workgroups_Notice_MembershipApproved", ct);
    }

    public async Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireCoordinator(workgroup, actorUserId, asAdmin) is { } roleRefusal) return new(Refusal: roleRefusal);

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        var name = await NameOfAsync(userId, ct);
        pending.LeftAt = clock.GetCurrentInstant();
        await repository.UpdateMembersAsync([pending], ct);
        ct = CancellationToken.None;
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupMembershipDeclined, AuditEntityTypes.Workgroup, workgroup.Id,
            $"Declined the join request of {name}", actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditEntityTypes.User);
        await NotifyAsync([userId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
            "Workgroups_Notice_MembershipDeclined", ToInfo(workgroup), body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireCoordinator(workgroup, actorUserId, asAdmin) is { } roleRefusal) return new(Refusal: roleRefusal);
        var target = await ResolveMemberTargetAsync(userId, ct);
        if (target is null)
            return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        userId = target.Value;

        var existing = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (existing is { IsPending: not true })
            return new(Refusal: new(WorkgroupErrorKeys.AlreadyAMember));

        // A pending request from the same person is approved by the add.
        var member = existing ?? NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, clock.GetCurrentInstant(), isPending: true);
        return await AdmitAsync(workgroup, member, actorUserId, WorkgroupsAuditActions.WorkgroupMemberAdded,
            "Workgroups_Notice_MemberAdded", ct, isNew: existing is null);
    }

    /// <summary>The one place a person becomes a member.</summary>
    private async Task<WorkgroupMutationResult> AdmitAsync(
        Workgroup workgroup, WorkgroupMember member, Guid actorUserId, AuditActionDefinition auditAction,
        string noticeKey, CancellationToken ct, bool isNew = false)
    {
        var name = await NameOfAsync(member.UserId, ct);
        var now = clock.GetCurrentInstant();
        member.IsPending = null;
        member.JoinedAt = now;
        if (isNew) await repository.AddMemberAsync(member, ct);
        else await repository.UpdateMembersAsync([member], ct);
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberJoined, now, name, ct);
        // Related entity is the person admitted: their GDPR export selects audit rows by that column.
        await auditLog.LogAsync(auditAction, AuditEntityTypes.Workgroup, workgroup.Id,
            $"{name} admitted to the group", actorUserId,
            relatedEntityId: member.UserId, relatedEntityType: AuditEntityTypes.User);
        await NotifyAsync([member.UserId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
            noticeKey, ToInfo(workgroup), body: null, ct);
        await RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (!asAdmin && RequireAcceptsMemberWork(workgroup) is { } stateRefusal)
            return new(Refusal: stateRefusal);

        var leaving = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null);
        if (leaving is null) return new(Refusal: new(WorkgroupErrorKeys.NotAMember));

        var now = clock.GetCurrentInstant();
        var changed = new List<WorkgroupMember> { leaving };
        leaving.LeftAt = now;

        if (leaving.IsPending == true)
        {
            await repository.UpdateMembersAsync(changed, ct);
            return new();
        }

        var remainingCoordinators = workgroup.Members
            .Where(m => m.LeftAt is null && m.Id != leaving.Id && m.Role == WorkgroupMemberRole.Coordinator)
            .ToList();

        WorkgroupMember? promoted = null;
        if (leaving.Role == WorkgroupMemberRole.Coordinator && remainingCoordinators.Count == 0)
        {
            // The last coordinator names a successor. An admin may override, leaving the group coordinatorless on
            // purpose so the Secretary can appoint someone.
            if (replacementCoordinatorUserId is { } replacementId)
            {
                promoted = workgroup.Members
                    .FirstOrDefault(m => m.UserId == replacementId && m.LeftAt is null && m.IsPending != true && m.Id != leaving.Id);
                if (promoted is null) return new(Refusal: new(WorkgroupErrorKeys.CoordinatorsMustBeMembers));
                promoted.Role = WorkgroupMemberRole.Coordinator;
                changed.Add(promoted);
            }
            else if (!asAdmin)
            {
                return new(Refusal: new(WorkgroupErrorKeys.LastCoordinatorNeedsReplacement));
            }
        }

        var leavingName = await NameOfAsync(userId, ct);
        var promotedName = promoted is null ? null : await NameOfAsync(promoted.UserId, ct);
        await repository.UpdateMembersAsync(changed, ct);
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberLeft, now, leavingName, ct);
        if (promoted is not null)
        {
            await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
                promotedName, ct);
            await AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
                $"Coordination handed to {promotedName} when the last coordinator left",
                userId);
        }

        await RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var now = clock.GetCurrentInstant();
        var info = ToInfo(workgroup);

        await repository.AddLogEntryAsync(new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = WorkgroupLogKind.StatusRequested,
            OccurredOn = now.InUtc().Date,
            Body = Trimmed(question),
            AuthorUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
        ct = CancellationToken.None;

        await NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupReportingDue,
            "Workgroups_Todo_StatusRequested_Title", info, Trimmed(question), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateRegisterFields(edit.Name, edit.Purpose, edit.Deliverable, edit.DiscordChannelUrl) is { } fieldsRefusal) return new(Refusal: fieldsRefusal);

        var now = clock.GetCurrentInstant();
        // The deliverable sentence is the group's promise: changing any part of it is a scope change on the record.
        var scopeChanged = !string.Equals(workgroup.Deliverable, edit.Deliverable.Trim(), StringComparison.Ordinal)
            || workgroup.DeliverableKind != edit.DeliverableKind
            || workgroup.Audience != edit.Audience
            || workgroup.TargetDate != edit.TargetDate;

        if (!string.Equals(workgroup.Name, edit.Name.Trim(), StringComparison.Ordinal))
        {
            var slugResult = await ReserveSlugAsync(edit.Name, workgroup.Id, ct);
            if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
            workgroup.Slug = slugResult.Value!;
        }

        ApplyFields(workgroup, edit.Name, edit.Purpose, edit.Deliverable, edit.DeliverableKind,
            edit.Audience, edit.TargetDate, edit.DiscordChannelUrl, edit.IsSensitive);
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        ct = CancellationToken.None;

        if (scopeChanged)
        {
            await AddSystemEntryAsync(workgroup, WorkgroupLogKind.ScopeChanged, now,
                $"By {workgroup.TargetDate?.ToInvariantLongDate() ?? "no date yet"} we will deliver "
                    + $"{workgroup.Deliverable} to the {workgroup.Audience}.",
                ct, authorUserId: actorUserId);
        }
        return new();
    }

    public async Task<WorkgroupMutationResult> SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinatorUserIds);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (!asAdmin && RequireAcceptsMemberWork(workgroup) is { } stateRefusal)
            return new(Refusal: stateRefusal);

        var wanted = coordinatorUserIds.Distinct().ToList();
        if (wanted.Count is < 1 or > 2)
            return new(Refusal: new(WorkgroupErrorKeys.CoordinatorCount));

        var current = workgroup.Members.Where(m => m.LeftAt is null && m.IsPending != true).ToList();
        if (wanted.Exists(id => current.TrueForAll(m => m.UserId != id)))
            return new(Refusal: new(WorkgroupErrorKeys.CoordinatorsMustBeMembers));

        var now = clock.GetCurrentInstant();
        var changed = new List<WorkgroupMember>();
        foreach (var member in current)
        {
            var role = wanted.Contains(member.UserId)
                ? WorkgroupMemberRole.Coordinator
                : WorkgroupMemberRole.Member;
            if (member.Role == role)
                continue;

            member.Role = role;
            changed.Add(member);
        }

        if (changed.Count == 0)
            return new();

        var names = await NamesOfAsync(wanted, ct);
        await repository.UpdateMembersAsync(changed, ct);
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
            string.Join(", ", names), ct, authorUserId: asAdmin ? null : actorUserId);
        await AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
            $"Coordinators set to {string.Join(", ", names)}", actorUserId);

        var info = ToInfo(workgroup);
        var affected = changed.Select(m => m.UserId).Distinct().ToList();
        await NotifyAsync(affected, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_CoordinatorChanged", info, string.Join(", ", names), ct);
        await EmailAsync(affected, WorkgroupNoticeKind.CoordinatorsChanged, info,
            string.Join(", ", names), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        var now = clock.GetCurrentInstant();
        var meeting = new WorkgroupMeeting
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyMeetingFields(meeting, save);
        await repository.AddMeetingAsync(meeting, ct);

        return new(meeting.Id);
    }

    public async Task<WorkgroupMutationResult> UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        ApplyMeetingFields(meeting, save);
        meeting.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateMeetingAsync(meeting, ct);

        // The row is overwritten, so the audit entry is the only record of the meeting the members were told about.
        await AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingUpdated, workgroup,
            $"Edited the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default)
    {
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await RequireActiveAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var now = clock.GetCurrentInstant();
        meeting.DeletedAt = now;
        meeting.UpdatedAt = now;
        await repository.UpdateMeetingAsync(meeting, ct);

        await AuditAsync(WorkgroupsAuditActions.WorkgroupMeetingDeleted, workgroup,
            $"Deleted the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(save.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);
        if (string.IsNullOrWhiteSpace(save.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (save.Body.Trim().Length > 16000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 16000));

        var now = clock.GetCurrentInstant();
        var entry = new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = save.Kind,
            OccurredOn = save.OccurredOn,
            Title = Trimmed(save.Title),
            Body = save.Body.Trim(),
            AuthorUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddLogEntryAsync(entry, ct);

        return new(entry.Id);
    }

    public async Task<WorkgroupMutationResult> UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);
        if (RequireMemberKind(save.Kind) is { } newKindRefusal) return new(Refusal: newKindRefusal);
        if (string.IsNullOrWhiteSpace(save.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (save.Body.Trim().Length > 16000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 16000));

        entry.Kind = save.Kind;
        entry.OccurredOn = save.OccurredOn;
        entry.Title = Trimmed(save.Title);
        entry.Body = save.Body.Trim();
        entry.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateLogEntryAsync(entry, ct);

        // The log keeps no history of its own, so without this an edit is as invisible as a deletion.
        await AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryUpdated, workgroup,
            $"Edited the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);

        await repository.DeleteLogEntryAsync(entryId, ct);
        // The log keeps no tombstone, so the audit trail is where the deletion stays visible.
        await AuditAsync(WorkgroupsAuditActions.WorkgroupLogEntryDeleted, workgroup,
            $"Deleted the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        // The entry is a trusted record naming a survey, so it must be one the actor authored.
        var survey = (await surveys.GetSummariesAsync(ct))
            .FirstOrDefault(s => s.Id == surveyId);
        if (survey is null || survey.CreatedByUserId != actorUserId)
            return new(Refusal: new(WorkgroupErrorKeys.SurveyNotYours));

        var now = clock.GetCurrentInstant();
        await repository.AddLogEntryAsync(new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Kind = WorkgroupLogKind.SurveySubmitted,
            OccurredOn = now.InUtc().Date,
            AuthorUserId = actorUserId,
            SurveyId = surveyId,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default)
    {
        // Quiet is the Secretary's close, never a member's own ending.
        if (reason is not (WorkgroupDormantReason.Delivered or WorkgroupDormantReason.Abandoned))
            return new(Refusal: new(WorkgroupErrorKeys.DoneReasonInvalid));

        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        // Ending while a comment period runs would cut short a window the group promised publicly.
        var now = clock.GetCurrentInstant();
        if (workgroup.Documents.Any(d => d.CommentsCloseAt is { } closes && closes > now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        await EndAsync(workgroup, actorUserId, reason, reasons: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<HoldedExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (ValidateBudgetAmount(save.Amount) is { } budgetRefusal)
            return new(Refusal: budgetRefusal);

        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        // Finance first, before any write: a failed create or unknown account leaves the row as it was.
        HoldedExpenseAccountRef? account = null;
        var needsAccount = save.Amount is not null
            && (workgroup.HoldedAccountNumber is null
                || (save.ExistingAccountNum is { } wanted && wanted != workgroup.HoldedAccountNumber));
        if (needsAccount)
        {
            // Account resolution can create remote state; finish its local binding and record.
            ct = CancellationToken.None;
            var accountResult = await ResolveBudgetAccountAsync(workgroup, save.ExistingAccountNum, ct);
            if (accountResult.Refusal is { } accountRefusal) return new(Refusal: accountRefusal);
            account = accountResult.Value!;
        }

        var now = clock.GetCurrentInstant();
        workgroup.BudgetAmount = save.Amount;
        if (account is not null)
        {
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
        }
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.BudgetSet, now, BudgetLogBody(workgroup), ct,
            authorUserId: actorUserId);
        await AuditAsync(WorkgroupsAuditActions.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);
        return new(account);
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(CancellationToken ct = default)
    {
        try { return await holded.ListExpenseAccountsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Holded chart unavailable; the link-existing picker is empty");
            return [];
        }
    }

    /// <summary>Finance faults log and return retry guidance; cancellation propagates.</summary>
    private async Task<WorkgroupMutationResult<HoldedExpenseAccountRef>> ResolveBudgetAccountAsync(
        Workgroup workgroup, int? existingAccountNum, CancellationToken ct)
    {
        try
        {
            return new(await finance.CreateOrLinkExpenseAccountAsync(
                $"Workgroups / {workgroup.Name}", existingAccountNum, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Finance could not resolve a Holded account for workgroup {WorkgroupId}", workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.BudgetAccountFailed));
        }
    }

    private static WorkgroupRefusal? ValidateBudgetAmount(decimal? amount)
    {
        if (amount is < 0) return new(WorkgroupErrorKeys.BudgetNegative);
        if (amount is { } value && decimal.Round(value, 2) != value)
            return new("Budget amounts must have no more than two decimal places.") { IsOperatorMessage = true };
        return null;
    }

    // Read in six cultures under the localized "Budget set" badge, so it carries figures only, never English prose.
    private static string BudgetLogBody(Workgroup w) => w.BudgetAmount is { } amount
        ? $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR · {w.HoldedAccountNumber}"
        : "—";

    private static string BudgetAuditSummary(Workgroup w) => w.BudgetAmount is { } amount
        ? $"Budget set to {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR, account {w.HoldedAccountNumber}"
        : "Budget cleared";

    public async Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default)
    {
        var stored = await repository.GetRootDriveFolderIdAsync(ct)
            // Legacy Settings value, used until an admin saves a local one.
            ?? await settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);
        // Values saved before the setter normalized may still hold the pasted URL.
        return stored is null ? null : NormalizeFolderId(stored);
    }

    public async Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        var id = NormalizeFolderId(folderId);
        // A missing root folder blocks every registration, so refuse it now rather than then.
        if (!await googleSync.IsFolderReachableAsync(id, ct))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderUnreachable));

        await repository.SetRootDriveFolderIdAsync(id, ct);
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty,
            $"Root Drive folder set to {id}", actorUserId);
        return new();
    }

    /// <summary>Secretaries paste the folder's browser URL as often as its id; only the id is kept.</summary>
    private static string NormalizeFolderId(string value) =>
        DriveFolderUrlId.Match(value) is { Success: true } m ? m.Groups["id"].Value : value.Trim();

    private static readonly Regex DriveFolderUrlId = new(@"(?:/folders/|[?&]id=)(?<id>[A-Za-z0-9_-]+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
}
