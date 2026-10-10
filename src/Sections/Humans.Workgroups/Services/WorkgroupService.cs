using System.Globalization;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Helpers;
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

/// <summary>
/// The register's rules: apply, register, refer, refuse, withdraw, close, reactivate;
/// join and leave with the one-or-two-coordinators invariant; meetings, the log, and the
/// documents with their comment period. Registration is administrative recognition only
/// (Board Resolution clause 4), so every decision here is a human's — the section
/// notifies, flags and records, and never decides.
/// </summary>
/// <remarks>
/// EF-free: the repository hands over detached entities and takes them back. Every
/// lifecycle step writes a system log entry, an audit entry, and the §14 notifications;
/// every change to who may see a group's Drive folder asks GoogleIntegration for a sync.
/// </remarks>
[CrossSectionWrite("Registration creates the group's Drive subfolder and requests Drive access syncs through IGoogleSyncService; SetBudgetAsync creates or links the group's Holded expense account through IHoldedFinanceService, and the lifecycle steps retire or restore it.")]
internal sealed partial class WorkgroupService(
    IWorkgroupRepository repository,
    IUserServiceRead users,
    ISurveyAnalysisRead surveys,
    IUserEmailService userEmails,
    IRoleAssignmentServiceRead roles,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    IHoldedFinanceService finance,
    IHoldedClient holded,
    INotificationService notifications,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<WorkgroupService> logger) : IWorkgroupService
{
    /// <summary>Route segments a group slug may not take.</summary>
    private static readonly string[] ReservedSlugs = ["apply", "admin"];

    // ── Reads ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        var graph = await repository.GetGraphAsync(ct);
        return graph.Workgroups.Select(ToInfo).ToList();
    }

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var workgroup = await repository.GetWorkgroupBySlugAsync(slug, ct);
        return workgroup is null ? null : ToInfo(workgroup);
    }

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


    // ── Applying and joining ──────────────────────────────────────────────

    public async Task<WorkgroupMutationResult<Guid>> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(application);
        if (ValidateApplication(application) is { } applicationRefusal) return new(Refusal: applicationRefusal);

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
            application.DiscordChannelUrl);

        // The applicant is a coordinator from the start; the optional second name joins with them.
        workgroup.Members.Add(NewMember(workgroup.Id, actorUserId, WorkgroupMemberRole.Coordinator, now));
        if (application.SecondCoordinatorUserId is { } second && second != actorUserId)
            workgroup.Members.Add(NewMember(workgroup.Id, second, WorkgroupMemberRole.Coordinator, now));

        workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.Applied, now,
            body: null));

        await repository.AddWorkgroupAsync(workgroup, ct);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(NotificationSource.WorkgroupRegistrationPending,
            $"Working group applied: {workgroup.Name}", info,
            "A new working group is waiting on the Secretary.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Applied, info, detail: null, ct);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        if (workgroup.Members.Any(m => m.UserId == userId && m.LeftAt is null))
            return new(Refusal: new(WorkgroupErrorKeys.AlreadyAMember));

        var name = await NameOfAsync(userId, ct);
        var now = clock.GetCurrentInstant();
        await repository.AddMemberAsync(
            NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, now), ct);
        // Membership has committed; its log and follow-up work must survive a closed tab.
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberJoined, now, name, ct);
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

        var remainingCoordinators = workgroup.Members
            .Where(m => m.LeftAt is null && m.Id != leaving.Id && m.Role == WorkgroupMemberRole.Coordinator)
            .ToList();

        WorkgroupMember? promoted = null;
        if (leaving.Role == WorkgroupMemberRole.Coordinator && remainingCoordinators.Count == 0)
        {
            // One or two coordinators at all times: the last one names a successor.
            // An admin may override, which leaves the group coordinatorless on purpose so
            // the Secretary can appoint someone.
            if (replacementCoordinatorUserId is { } replacementId)
            {
                promoted = workgroup.Members
                    .FirstOrDefault(m => m.UserId == replacementId && m.LeftAt is null && m.Id != leaving.Id);
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
        // Membership has committed; finish its log, audit and follow-up work.
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberLeft, now, leavingName, ct);
        if (promoted is not null)
        {
            await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
                promotedName, ct);
            await AuditAsync(AuditAction.WorkgroupCoordinatorsChanged, workgroup,
                $"Coordination handed to {promotedName} when the last coordinator left",
                userId);
        }

        await RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

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
        // The change committed; finish its log or notice independently of the browser.
        ct = CancellationToken.None;

        await NotifyAsync(info.CoordinatorUserIds(), NotificationSource.WorkgroupReportingDue,
            "Workgroups_Todo_StatusRequested_Title", info, Trimmed(question), ct);
        return new();
    }

    // ── Member work on the group page ─────────────────────────────────────

    public async Task<WorkgroupMutationResult> EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (ValidateRegisterFields(edit.Name, edit.Purpose, edit.Deliverable) is { } fieldsRefusal) return new(Refusal: fieldsRefusal);

        var now = clock.GetCurrentInstant();
        // The deliverable sentence is the group's promise; changing any part of it is a
        // scope change and goes on the record.
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
            edit.Audience, edit.TargetDate, edit.DiscordChannelUrl);
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        // The change committed; finish its log or notice independently of the browser.
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

        var current = workgroup.Members.Where(m => m.LeftAt is null).ToList();
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
        // Membership has committed; finish its log, audit and follow-up work.
        ct = CancellationToken.None;
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
            string.Join(", ", names), ct, authorUserId: asAdmin ? null : actorUserId);
        await AuditAsync(AuditAction.WorkgroupCoordinatorsChanged, workgroup,
            $"Coordinators set to {string.Join(", ", names)}", actorUserId);

        var info = ToInfo(workgroup);
        var affected = changed.Select(m => m.UserId).Distinct().ToList();
        await NotifyAsync(affected, NotificationSource.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_CoordinatorChanged", info, string.Join(", ", names), ct);
        await EmailAsync(affected, WorkgroupNoticeKind.CoordinatorsChanged, info,
            string.Join(", ", names), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
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
        var workgroupResult = await RequireAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (ValidateMeeting(save) is { } meetingValidationRefusal) return new(Refusal: meetingValidationRefusal);

        ApplyMeetingFields(meeting, save);
        meeting.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateMeetingAsync(meeting, ct);

        // The row is overwritten, so the audit entry is the only record that the meeting
        // the members were told about is not the one on the page now.
        await AuditAsync(AuditAction.WorkgroupMeetingUpdated, workgroup,
            $"Edited the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default)
    {
        var meetingResult = await RequireLiveMeetingAsync(meetingId, ct);
        if (meetingResult.Refusal is { } meetingRefusal) return new(Refusal: meetingRefusal);
        var meeting = meetingResult.Value!;
        var workgroupResult = await RequireAsync(meeting.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        var now = clock.GetCurrentInstant();
        meeting.DeletedAt = now;
        meeting.UpdatedAt = now;
        await repository.UpdateMeetingAsync(meeting, ct);

        await AuditAsync(AuditAction.WorkgroupMeetingDeleted, workgroup,
            $"Deleted the meeting of {meeting.StartUtc.InUtc().Date.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupMeeting, meeting.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
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
        var workgroupResult = await RequireAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        // System entries are the section's record of what it did; they never change.
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

        // The log keeps no history of its own, so an edit is as invisible as a deletion
        // without this.
        await AuditAsync(AuditAction.WorkgroupLogEntryUpdated, workgroup,
            $"Edited the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await repository.GetLogEntryAsync(entryId, ct);
        if (entry is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireAsync(entry.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (RequireMemberKind(entry.Kind) is { } kindRefusal) return new(Refusal: kindRefusal);

        await repository.DeleteLogEntryAsync(entryId, ct);
        // The log keeps no tombstone, so the audit trail is where the deletion stays visible.
        await AuditAsync(AuditAction.WorkgroupLogEntryDeleted, workgroup,
            $"Deleted the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
        return new();
    }

    public async Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        // The entry is a trusted system record naming a survey, so the id has to be one the
        // member actually authored — otherwise the log can point at somebody else's survey, or
        // at nothing at all. Peter 2026-09-14: author == actor is the whole check.
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

        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        // Same gate as delivering a document (design §7): ending the group while a comment
        // period is still running would cut short a window the group promised publicly.
        var now = clock.GetCurrentInstant();
        if (workgroup.Documents.Any(d => d.CommentsCloseAt is { } closes && closes > now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        await EndAsync(workgroup, actorUserId, reason, reasons: null, ct);
        return new();
    }

    // ── Budget ────────────────────────────────────────────────────────────

    public async Task<WorkgroupMutationResult<HoldedExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Amount is < 0)
            return new(Refusal: new(WorkgroupErrorKeys.BudgetNegative));

        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        // Finance is asked first, before anything is written: a failed create or an unknown
        // account number leaves the row exactly as it was.
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
        // Amount-only saves also need their log and audit after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.BudgetSet, now, BudgetLogBody(workgroup), ct,
            authorUserId: actorUserId);
        await AuditAsync(AuditAction.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);
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

    // The History body is read in six cultures under the localized "Budget set" badge, so it
    // carries only figures, never English prose.
    private static string BudgetLogBody(Workgroup w) => w.BudgetAmount is { } amount
        ? $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR · {w.HoldedAccountNumber}"
        : "—";

    private static string BudgetAuditSummary(Workgroup w) => w.BudgetAmount is { } amount
        ? $"Budget set to {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR, account {w.HoldedAccountNumber}"
        : "Budget cleared";

    // ── Settings ──────────────────────────────────────────────────────────

    public async Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default)
    {
        var stored = await repository.GetRootDriveFolderIdAsync(ct)
            // Existing installations keep their configured root until an admin saves it
            // locally. New state is section-owned; there is no automatic data backfill.
            ?? await settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);
        // Values saved before the setter normalized may still hold the pasted URL.
        return stored is null ? null : NormalizeFolderId(stored);
    }

    public async Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        var id = NormalizeFolderId(folderId);
        await repository.SetRootDriveFolderIdAsync(id, ct);
        await auditLog.LogAsync(AuditAction.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty,
            $"Root Drive folder set to {id}", actorUserId);
        return new();
    }

    /// <summary>Secretaries paste the folder's browser URL as often as its id; only the id is kept.</summary>
    private static string NormalizeFolderId(string value) =>
        DriveFolderUrlId.Match(value) is { Success: true } m ? m.Groups["id"].Value : value.Trim();

    private static readonly Regex DriveFolderUrlId = new(@"(?:/folders/|[?&]id=)(?<id>[A-Za-z0-9_-]+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
}
