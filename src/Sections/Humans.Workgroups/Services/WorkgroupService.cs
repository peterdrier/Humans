using static Humans.Workgroups.Services.WorkgroupAccess;
using static Humans.Workgroups.Services.WorkgroupJournal;
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
    IRoleAssignmentService roles,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    IGoogleSyncWrite googleSyncWrite,
    IFinanceWrite finance,
    IHoldedService holded,
    INotificationService notifications,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<WorkgroupService> logger) : IWorkgroupService
{
    private WorkgroupActivity Activity => new(repository, access, journal, surveys, clock);
    private readonly WorkgroupAccess access = new(repository);
    private readonly WorkgroupJournal journal = new(auditLog);
    private WorkgroupDocumentWorkflow DocumentWorkflow => new(repository, access, journal, notices, clock);
    private readonly WorkgroupDataExport dataExport = new(repository);
    private readonly WorkgroupRegisterReader registerReader = new(repository);

    private readonly WorkgroupNotices notices = new(users, roles, notifications, email, emailFactory, logger);

    public Task RunDailyRhythmAsync(CancellationToken ct = default) =>
        new WorkgroupRhythm(repository, notices, auditLog, clock, logger).RunDailyRhythmAsync(ct);

    private static readonly string[] ReservedSlugs = ["apply", "admin"];

    public Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default) =>
        registerReader.GetRegisterAsync(ct);

    public Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        registerReader.GetBySlugAsync(slug, ct);

    public Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        registerReader.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default) =>
        registerReader.GetForMemberAsync(userId, ct);

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
        await notices.NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Working group applied: {workgroup.Name}", info,
            "A new working group is waiting on the Secretary.", ct);
        await notices.EmailBoardAsync(WorkgroupNoticeKind.Applied, info, detail: null, ct);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
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
        await notices.NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupMembershipRequested,
            "Workgroups_Notice_MembershipRequested", info, body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireMembershipManagerAsync(workgroupId, actorUserId, asAdmin, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        return await AdmitAsync(workgroup, pending, actorUserId, WorkgroupsAuditActions.WorkgroupMembershipApproved,
            "Workgroups_Notice_MembershipApproved", ct);
    }

    public async Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireMembershipManagerAsync(workgroupId, actorUserId, asAdmin, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        var name = await NameOfAsync(userId, ct);
        pending.LeftAt = clock.GetCurrentInstant();
        await repository.UpdateMembersAsync([pending], ct);
        ct = CancellationToken.None;
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupMembershipDeclined, AuditEntityTypes.Workgroup, workgroup.Id,
            $"Declined the join request of {name}", actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
        await notices.NotifyAsync([userId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
            "Workgroups_Notice_MembershipDeclined", ToInfo(workgroup), body: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireMembershipManagerAsync(workgroupId, actorUserId, asAdmin, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
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
        var entries = new[] { SystemEntry(workgroup.Id, WorkgroupLogKind.MemberJoined, now, name) };
        if (isNew) await repository.AddMemberAsync(member, ct, entries);
        else await repository.UpdateMembersAsync([member], ct, entries);
        ct = CancellationToken.None;
        // Related entity is the person admitted: their GDPR export selects audit rows by that column.
        await auditLog.LogAsync(auditAction, AuditEntityTypes.Workgroup, workgroup.Id,
            $"{name} admitted to the group", actorUserId,
            relatedEntityId: member.UserId, relatedEntityType: AuditSubjectTypes.User);
        await notices.NotifyAsync([member.UserId], WorkgroupsNotificationSources.WorkgroupMembershipDecided,
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
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
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
        var entries = new List<WorkgroupLogEntry>
        {
            SystemEntry(workgroup.Id, WorkgroupLogKind.MemberLeft, now, leavingName)
        };
        if (promoted is not null)
            entries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.CoordinatorChanged, now, promotedName));
        await repository.UpdateMembersAsync(changed, ct, entries);
        ct = CancellationToken.None;
        if (promoted is not null)
        {
            await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
                $"Coordination handed to {promotedName} when the last coordinator left",
                userId);
        }

        await RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
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

        await notices.NotifyAsync(info.CoordinatorUserIds(), WorkgroupsNotificationSources.WorkgroupReportingDue,
            "Workgroups_Todo_StatusRequested_Title", info, Trimmed(question), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
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
        await repository.UpdateWorkgroupAsync(workgroup, ct, scopeChanged
            ? [SystemEntry(workgroup.Id, WorkgroupLogKind.ScopeChanged, now,
                $"By {workgroup.TargetDate?.ToInvariantLongDate() ?? "no date yet"} we will deliver "
                    + $"{workgroup.Deliverable} to the {workgroup.Audience}.", authorUserId: actorUserId)]
            : null);
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
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
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
        await repository.UpdateMembersAsync(changed, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.CoordinatorChanged, now,
                string.Join(", ", names), authorUserId: asAdmin ? null : actorUserId)]);
        ct = CancellationToken.None;
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupCoordinatorsChanged, workgroup,
            $"Coordinators set to {string.Join(", ", names)}", actorUserId);

        var info = ToInfo(workgroup);
        var affected = changed.Select(m => m.UserId).Distinct().ToList();
        await notices.NotifyAsync(affected, WorkgroupsNotificationSources.WorkgroupRegistrationDecided,
            "Enum_WorkgroupLogKind_CoordinatorChanged", info, string.Join(", ", names), ct);
        await notices.EmailAsync(affected, WorkgroupNoticeKind.CoordinatorsChanged, info,
            string.Join(", ", names), ct);
        return new();
    }

    public Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        Activity.CreateMeetingAsync(workgroupId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        Activity.UpdateMeetingAsync(meetingId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default) =>
        Activity.DeleteMeetingAsync(meetingId, actorUserId, ct);

    public Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        Activity.AddLogEntryAsync(workgroupId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        Activity.UpdateLogEntryAsync(entryId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default) =>
        Activity.DeleteLogEntryAsync(entryId, actorUserId, ct);

    public Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default) =>
        Activity.LinkSurveyAsync(workgroupId, actorUserId, surveyId, ct);

    public async Task<WorkgroupMutationResult> MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default)
    {
        // Quiet is the Secretary's close, never a member's own ending.
        if (reason is not (WorkgroupDormantReason.Delivered or WorkgroupDormantReason.Abandoned))
            return new(Refusal: new(WorkgroupErrorKeys.DoneReasonInvalid));

        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        // Ending while a comment period runs would cut short a window the group promised publicly.
        var now = clock.GetCurrentInstant();
        if (workgroup.Documents.Any(d => d.CommentsCloseAt is { } closes && closes > now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        await EndAsync(workgroup, actorUserId, reason, reasons: null, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<ExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (ValidateBudgetAmount(save.Amount) is { } budgetRefusal)
            return new(Refusal: budgetRefusal);

        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        // Finance first, before any write: a failed create or unknown account leaves the row as it was.
        ExpenseAccountRef? account = null;
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
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.BudgetSet, now, BudgetLogBody(workgroup),
                authorUserId: actorUserId)]);
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);
        return new(account);
    }

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> ListExpenseAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            return (await holded.GetChartAsync(ct))
                .Where(a => a.Number is >= 60000000 and <= 69999999 && !a.Archived)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Holded mirror unavailable; the link-existing picker is empty");
            return [];
        }
    }

    /// <summary>Finance faults log and return retry guidance; cancellation propagates.</summary>
    private async Task<WorkgroupMutationResult<ExpenseAccountRef>> ResolveBudgetAccountAsync(
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

    // Read in all supported cultures under the localized "Budget set" badge, so it carries figures only, never English prose.
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
