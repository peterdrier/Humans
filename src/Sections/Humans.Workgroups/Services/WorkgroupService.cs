using static Humans.Workgroups.Services.WorkgroupAccess;
using static Humans.Workgroups.Services.WorkgroupJournal;
using System.Text.RegularExpressions;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Google.Contracts;
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
    IGoogleGroupSync googleGroupSync,
    IFinanceWrite finance,
    IHoldedService holded,
    INotificationService notifications,
    IEmailService email,
    WorkgroupsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<WorkgroupService> logger) : IWorkgroupService
{
    private WorkgroupMembership Membership => new(repository, access, journal, notices, GoogleResources, users, auditLog, clock);
    private WorkgroupGoogleResources GoogleResources => new(repository, settings, googleSync, googleSyncWrite, googleGroupSync, auditLog, logger);
    private WorkgroupBudgets Budgets => new(repository, access, journal, finance, holded, clock, logger);
    private WorkgroupActivity Activity => new(repository, access, journal, surveys, clock);
    private readonly WorkgroupAccess access = new(repository, users);
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

        workgroup.Members.Add(WorkgroupMembership.NewMember(workgroup.Id, actorUserId, WorkgroupMemberRole.Coordinator, now));
        if (application.SecondCoordinatorUserId is { } second && second != actorUserId)
            workgroup.Members.Add(WorkgroupMembership.NewMember(workgroup.Id, second, WorkgroupMemberRole.Coordinator, now));

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

    public Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default) =>
        Membership.JoinAsync(workgroupId, userId, ct);

    public Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        Membership.ApproveMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct);

    public Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        Membership.DeclineMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct);

    public Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        Membership.AddMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct);

    public Task<WorkgroupMutationResult> LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        Membership.LeaveAsync(workgroupId, userId, replacementCoordinatorUserId, asAdmin, ct);

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

    public Task<WorkgroupMutationResult> SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        Membership.SetCoordinatorsAsync(workgroupId, actorUserId, coordinatorUserIds, asAdmin, ct);

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

    public Task<WorkgroupMutationResult<ExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default) =>
        Budgets.SetBudgetAsync(workgroupId, actorUserId, save, ct);

    public async Task<WorkgroupMutationResult> SetGoogleGroupAsync(
        Guid workgroupId, Guid actorUserId, string? prefix, CancellationToken ct = default)
    {
        var parsed = ParseGoogleGroupPrefix(prefix);
        if (parsed.Refusal is { } prefixRefusal) return new(Refusal: prefixRefusal);
        var newPrefix = parsed.Value;

        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        if (string.Equals(workgroup.GoogleGroupPrefix, newPrefix, StringComparison.Ordinal))
            return new();

        // Two groups on one address would fight over its membership in the fan-out.
        if (newPrefix is not null && (await registerReader.GetRegisterAsync(ct)).Any(w => w.Id != workgroup.Id
                && string.Equals(w.GoogleGroupEmail, $"{newPrefix}@{DomainConstants.GoogleGroupDomain}", StringComparison.OrdinalIgnoreCase)))
            return new(Refusal: new("Another workgroup already uses this email group.") { IsOperatorMessage = true });

        workgroup.GoogleGroupPrefix = newPrefix;
        workgroup.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        ct = CancellationToken.None;

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupGoogleGroupSet, workgroup,
            workgroup.GoogleGroupEmail is { } email ? $"Email group set to {email}" : "Email group cleared", actorUserId);
        await GoogleResources.RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    /// <summary>Same rule as a team's group prefix. Admins paste the full address as often as its local part; only our own domain is accepted.</summary>
    private static WorkgroupMutationResult<string?> ParseGoogleGroupPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new((string?)null);

        var prefix = value.Trim().ToLowerInvariant();
        var at = prefix.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            if (!string.Equals(prefix[(at + 1)..], DomainConstants.GoogleGroupDomain, StringComparison.Ordinal))
                return new(Refusal: new($"The email group must be on {DomainConstants.GoogleGroupDomain}.") { IsOperatorMessage = true });
            prefix = prefix[..at];
        }

        return prefix.Length <= 64 && GoogleGroupPrefixPattern.IsMatch(prefix)
            ? new(prefix)
            : new(Refusal: new("Use only lowercase letters, numbers and hyphens for the email group name.") { IsOperatorMessage = true });
    }

    private static readonly Regex GoogleGroupPrefixPattern =
        new("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    public Task<IReadOnlyList<HoldedChartAccountInfo>> ListExpenseAccountsAsync(CancellationToken ct = default) =>
        Budgets.ListExpenseAccountsAsync(ct);

    public Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default) =>
        GoogleResources.GetRootDriveFolderIdAsync(ct);

    public Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default) =>
        GoogleResources.SetRootDriveFolderIdAsync(folderId, actorUserId, ct);
}
