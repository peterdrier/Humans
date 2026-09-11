using Humans.Base.Extensions;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Helpers;
using Humans.Email.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
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
[CrossSectionWrite("Registration creates the group's Drive subfolder and requests Drive access syncs through IGoogleSyncService, and stores the root folder id through ISettingsService.")]
internal sealed partial class WorkgroupService(
    IWorkgroupRepository repository,
    IUserServiceRead users,
    IUserEmailService userEmails,
    IRoleAssignmentService roles,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    INotificationService notifications,
    IEmailService email,
    IEmailMessageFactory emailFactory,
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

    public async Task<bool> IsMemberAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroup = await repository.GetWorkgroupAsync(workgroupId, ct);
        return workgroup is not null
            && workgroup.Members.Any(m => m.UserId == userId && m.LeftAt is null);
    }

    // ── Applying and joining ──────────────────────────────────────────────

    public async Task<Guid> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ValidateApplication(application);

        var now = clock.GetCurrentInstant();
        var slug = await ReserveSlugAsync(application.Name, null, ct);

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

        return workgroup.Id;
    }

    public async Task JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

        if (workgroup.Members.Any(m => m.UserId == userId && m.LeftAt is null))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.AlreadyAMember);

        var now = clock.GetCurrentInstant();
        await repository.AddMemberAsync(
            NewMember(workgroup.Id, userId, WorkgroupMemberRole.Member, now), ct);
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberJoined, now,
            await NameOfAsync(userId, ct), ct);
        await RequestDriveSyncAsync(workgroup, ct);
    }

    public async Task LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        if (!asAdmin)
            RequireAcceptsMemberWork(workgroup);

        var leaving = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotAMember);

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
                    .FirstOrDefault(m => m.UserId == replacementId && m.LeftAt is null && m.Id != leaving.Id)
                    ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.CoordinatorsMustBeMembers);
                promoted.Role = WorkgroupMemberRole.Coordinator;
                changed.Add(promoted);
            }
            else if (!asAdmin)
            {
                throw new WorkgroupRuleException(WorkgroupErrorKeys.LastCoordinatorNeedsReplacement);
            }
        }

        await repository.UpdateMembersAsync(changed, ct);
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.MemberLeft, now,
            await NameOfAsync(userId, ct), ct);
        if (promoted is not null)
        {
            await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
                await NameOfAsync(promoted.UserId, ct), ct);
            await AuditAsync(AuditAction.WorkgroupCoordinatorsChanged, workgroup,
                $"Coordination handed to {await NameOfAsync(promoted.UserId, ct)} when the last coordinator left",
                userId);
        }

        await RequestDriveSyncAsync(workgroup, ct);
    }

    public async Task RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

        var now = clock.GetCurrentInstant();
        var info = ToInfo(workgroup);
        if (info.StatusRequestOnCooldownFor(actorUserId, now))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.StatusRequestOnCooldown);

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

        await NotifyAsync(info.CoordinatorUserIds(), NotificationSource.WorkgroupReportingDue,
            $"Status update requested: {workgroup.Name}", info,
            Trimmed(question) ?? "A member asked how the group is getting on.", ct);
    }

    // ── Member work on the group page ─────────────────────────────────────

    public async Task EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        ValidateRegisterFields(edit.Name, edit.Purpose, edit.Deliverable);

        var now = clock.GetCurrentInstant();
        // The deliverable sentence is the group's promise; changing any part of it is a
        // scope change and goes on the record.
        var scopeChanged = !string.Equals(workgroup.Deliverable, edit.Deliverable.Trim(), StringComparison.Ordinal)
            || workgroup.DeliverableKind != edit.DeliverableKind
            || workgroup.Audience != edit.Audience
            || workgroup.TargetDate != edit.TargetDate;

        if (!string.Equals(workgroup.Name, edit.Name.Trim(), StringComparison.Ordinal))
            workgroup.Slug = await ReserveSlugAsync(edit.Name, workgroup.Id, ct);

        ApplyFields(workgroup, edit.Name, edit.Purpose, edit.Deliverable, edit.DeliverableKind,
            edit.Audience, edit.TargetDate, edit.DiscordChannelUrl);
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        if (scopeChanged)
        {
            await AddSystemEntryAsync(workgroup, WorkgroupLogKind.ScopeChanged, now,
                $"By {workgroup.TargetDate?.ToInvariantLongDate() ?? "no date yet"} we will deliver "
                    + $"{workgroup.Deliverable} to the {workgroup.Audience}.",
                ct, authorUserId: actorUserId);
        }
    }

    public async Task SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinatorUserIds);
        var workgroup = await RequireAsync(workgroupId, ct);
        if (!asAdmin)
            RequireAcceptsMemberWork(workgroup);

        var wanted = coordinatorUserIds.Distinct().ToList();
        if (wanted.Count is < 1 or > 2)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.CoordinatorCount);

        var current = workgroup.Members.Where(m => m.LeftAt is null).ToList();
        if (wanted.Exists(id => current.TrueForAll(m => m.UserId != id)))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.CoordinatorsMustBeMembers);

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
            return;

        await repository.UpdateMembersAsync(changed, ct);

        var names = await NamesOfAsync(wanted, ct);
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CoordinatorChanged, now,
            string.Join(", ", names), ct, authorUserId: asAdmin ? null : actorUserId);
        await AuditAsync(AuditAction.WorkgroupCoordinatorsChanged, workgroup,
            $"Coordinators set to {string.Join(", ", names)}", actorUserId);

        var info = ToInfo(workgroup);
        var affected = changed.Select(m => m.UserId).Distinct().ToList();
        await NotifyAsync(affected, NotificationSource.WorkgroupRegistrationDecided,
            $"Coordination changed: {workgroup.Name}", info,
            $"The coordinators are now {string.Join(", ", names)}.", ct);
        await EmailAsync(affected, WorkgroupNoticeKind.CoordinatorsChanged, info,
            string.Join(", ", names), ct);
    }

    public async Task<Guid> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        ValidateMeeting(save);

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

        // A meeting is a sign of life: it clears the dormancy flag like an Update does.
        await ClearDormancyFlagAsync(workgroup, now, ct);
        return meeting.Id;
    }

    public async Task UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var meeting = await repository.GetMeetingAsync(meetingId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(meeting.WorkgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        ValidateMeeting(save);

        ApplyMeetingFields(meeting, save);
        meeting.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateMeetingAsync(meeting, ct);
    }

    public async Task DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default)
    {
        var meeting = await repository.GetMeetingAsync(meetingId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(meeting.WorkgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

        var now = clock.GetCurrentInstant();
        meeting.DeletedAt = now;
        meeting.UpdatedAt = now;
        await repository.UpdateMeetingAsync(meeting, ct);
    }

    public async Task<Guid> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        RequireMemberKind(save.Kind);
        if (string.IsNullOrWhiteSpace(save.Body))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.BodyRequired);

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

        if (save.Kind == WorkgroupLogKind.Update)
            await ClearDormancyFlagAsync(workgroup, now, ct);

        return entry.Id;
    }

    public async Task UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var entry = await repository.GetLogEntryAsync(entryId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(entry.WorkgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        // System entries are the section's record of what it did; they never change.
        RequireMemberKind(entry.Kind);
        RequireMemberKind(save.Kind);
        if (string.IsNullOrWhiteSpace(save.Body))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.BodyRequired);

        entry.Kind = save.Kind;
        entry.OccurredOn = save.OccurredOn;
        entry.Title = Trimmed(save.Title);
        entry.Body = save.Body.Trim();
        entry.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateLogEntryAsync(entry, ct);
    }

    public async Task DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await repository.GetLogEntryAsync(entryId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(entry.WorkgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        RequireMemberKind(entry.Kind);

        await repository.DeleteLogEntryAsync(entryId, ct);
        // The log keeps no tombstone, so the audit trail is where the deletion stays visible.
        await AuditAsync(AuditAction.WorkgroupLogEntryDeleted, workgroup,
            $"Deleted the {entry.Kind} entry of {entry.OccurredOn.ToInvariantDate()}", actorUserId,
            AuditEntityTypes.WorkgroupLogEntry, entryId);
    }

    public async Task LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

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
    }

    public async Task MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default)
    {
        // Quiet is the Secretary's close, never a member's own ending.
        if (reason is not (WorkgroupDormantReason.Delivered or WorkgroupDormantReason.Abandoned))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.DoneReasonInvalid);

        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

        // Same gate as delivering a document (design §7): ending the group while a comment
        // period is still running would cut short a window the group promised publicly.
        var now = clock.GetCurrentInstant();
        if (workgroup.Documents.Any(d => d.CommentsCloseAt is { } closes && closes > now))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.CommentsStillOpen);

        await EndAsync(workgroup, actorUserId, reason, reasons: null, ct);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default) =>
        settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);

    public async Task SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.RootFolderNotConfigured);

        await settings.SetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, folderId.Trim(), ct);
        await auditLog.LogAsync(AuditAction.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty,
            $"Root Drive folder set to {folderId.Trim()}", actorUserId);
    }
}
