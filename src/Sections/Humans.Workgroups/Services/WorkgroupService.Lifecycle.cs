using static Humans.Workgroups.Services.WorkgroupAccess;
using static Humans.Workgroups.Services.WorkgroupJournal;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService
{
    public async Task<WorkgroupMutationResult> RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        // Registration completes even if the initiating request disconnects.
        ct = CancellationToken.None;
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred) is { } statusRefusal) return new(Refusal: statusRefusal);

        // The folder comes first: a failure here leaves the row as it was, so the Secretary can retry.
        if (workgroup.DriveFolderId is null)
        {
            var folderResult = await DriveFolders.CreateGroupFolderAsync(workgroup, ct);
            if (folderResult.Refusal is { } folderRefusal) return new(Refusal: folderRefusal);
            workgroup.DriveFolderId = folderResult.Value!;
        }

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Active;
        workgroup.RegisteredAt = now;
        workgroup.Reasons = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Registered, now, body: null)]);

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupRegistered, workgroup,
            $"Registered '{workgroup.Name}' with Drive folder {workgroup.DriveFolderId}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Registered, detail: null, ct);
        await DriveFolders.RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ReferAsync(
        Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Referred;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Referred, now, Trimmed(note))]);
        ct = CancellationToken.None;

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupReferred, workgroup,
            $"Referred '{workgroup.Name}' to the Board{(Trimmed(note) is { } n ? $": {n}" : "")}",
            actorUserId);

        var info = ToInfo(workgroup);
        await notices.NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
            $"Referred to the Board: {workgroup.Name}", info,
            Trimmed(note) ?? "A refusal ground may apply; the Board decides at its next meeting.", ct);
        await notices.EmailBoardAsync(WorkgroupNoticeKind.Referred, info, Trimmed(note), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> RefuseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Refused;
        workgroup.Reasons = reasons.Trim();
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Refused, now, reasons.Trim())]);
        ct = CancellationToken.None;

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupRefused, workgroup,
            $"Refused '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Refused, reasons.Trim(), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> WithdrawAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Withdrawn;
        workgroup.DormantReason = null;
        workgroup.Reasons = reasons.Trim();
        workgroup.EndedAt = now;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Withdrawn, now, reasons.Trim())],
            ClosePendingRequests(workgroup, now));
        ct = CancellationToken.None;
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupWithdrawn, workgroup,
            $"Withdrew registration of '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Withdrawn, reasons.Trim(), ct);
        // Withdrawn isn't Active, so the source stops claiming write access.
        await DriveFolders.RequestDriveSyncAsync(workgroup, ct);
        await Budgets.SetAccountActiveAsync(workgroup, isActive: false, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> CloseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Active) is { } statusRefusal) return new(Refusal: statusRefusal);

        // The member's own ending is MarkDoneAsync, which can't reach the Quiet reason.
        await EndAsync(workgroup, actorUserId, WorkgroupDormantReason.Quiet, reasons, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Active;
        workgroup.DormantReason = null;
        workgroup.EndedAt = null;
        workgroup.Reasons = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.Reactivated, now, body: null)]);
        ct = CancellationToken.None;

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupReactivated, workgroup,
            $"Reactivated '{workgroup.Name}'", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Reactivated, detail: null, ct);
        await DriveFolders.RequestDriveSyncAsync(workgroup, ct);
        await Budgets.SetAccountActiveAsync(workgroup, isActive: true, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> RegisterExistingAsync(
        Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(bootstrap);
        var validation = await ValidateApplicationAsync(bootstrap.Application, ct);
        if (validation.Refusal is { } applicationRefusal) return new(Refusal: applicationRefusal);
        var application = validation.Value!;
        if (await ResolveMemberTargetAsync(bootstrap.CoordinatorUserId, ct) is not { } coordinatorId)
            return new(Refusal: new(WorkgroupErrorKeys.NotFound));

        var now = clock.GetCurrentInstant();
        var slugResult = await ReserveSlugAsync(bootstrap.Application.Name, null, ct);
        if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
        var slug = slugResult.Value!;
        var workgroup = new Workgroup
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            // The application it never filed takes the same backdated date.
            Status = WorkgroupStatus.Active,
            AppliedByUserId = coordinatorId,
            AppliedAt = bootstrap.RegisteredAt,
            RegisteredAt = bootstrap.RegisteredAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyFields(workgroup, application.Name, application.Purpose, application.Deliverable,
            application.DeliverableKind, application.Audience, application.TargetDate,
            application.DiscordChannelUrl, application.IsSensitive);

        workgroup.Members.Add(NewMember(workgroup.Id, coordinatorId,
            WorkgroupMemberRole.Coordinator, bootstrap.RegisteredAt));
        if (application.SecondCoordinatorUserId is { } second && second != coordinatorId)
        {
            workgroup.Members.Add(NewMember(workgroup.Id, second,
                WorkgroupMemberRole.Coordinator, bootstrap.RegisteredAt));
        }

        workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.Registered,
            bootstrap.RegisteredAt, body: null));

        // Finance before anything durable: otherwise a Holded failure reports an error for a group that exists,
        // and a retry registers it twice.
        var budget = bootstrap.Budget is { Amount: not null } b ? b : null;
        if (budget is not null)
        {
            if (WorkgroupBudgets.ValidateBudgetAmount(budget.Amount) is { } budgetRefusal)
                return new(Refusal: budgetRefusal);
            var accountResult = await Budgets.ResolveBudgetAccountAsync(workgroup, budget.ExistingAccountNum, ct);
            if (accountResult.Refusal is { } accountRefusal) return new(Refusal: accountRefusal);
            var account = accountResult.Value!;
            workgroup.BudgetAmount = budget.Amount;
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
            workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.BudgetSet, now,
                WorkgroupBudgets.BudgetLogBody(workgroup), actorUserId));
        }

        var folderResult = await DriveFolders.CreateGroupFolderAsync(workgroup, ct);
        if (folderResult.Refusal is { } folderRefusal) return new(Refusal: folderRefusal);
        workgroup.DriveFolderId = folderResult.Value!;
        await repository.AddWorkgroupAsync(workgroup, ct);

        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupRegisteredExisting, workgroup,
            $"Registered the existing group '{workgroup.Name}', backdated to "
                + $"{bootstrap.RegisteredAt.InUtc().Date.ToInvariantDate()}",
            actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Registered, detail: null, ct);
        await DriveFolders.RequestDriveSyncAsync(workgroup, ct);
        if (budget is not null)
            await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupBudgetSet, workgroup, WorkgroupBudgets.BudgetAuditSummary(workgroup), actorUserId);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default)
    {
        if (RequireReasons(note) is { } noteRefusal)
            return new(Refusal: noteRefusal);

        var documentResult = await DocumentWorkflow.RequireDocumentAsync(documentId, ct, requireActive: false);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (document.Status != WorkgroupDocumentStatus.Delivered)
            return new(Refusal: new(WorkgroupErrorKeys.NotDelivered));

        var now = clock.GetCurrentInstant();
        document.Disposition = disposition;
        document.DispositionNote = note.Trim();
        document.DispositionAt = now;
        document.DispositionByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.DispositionRecorded, now,
                $"{disposition}: {note.Trim()}", documentId: document.Id)]);
        ct = CancellationToken.None;
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupDispositionRecorded, workgroup,
            $"Recorded {disposition} on '{document.Title}': {note.Trim()}", actorUserId,
            AuditEntityTypes.WorkgroupDocument, document.Id);

        var info = ToInfo(workgroup);
        await notices.NotifyAsync(info.CurrentMemberUserIds(), WorkgroupsNotificationSources.WorkgroupDispositionRecorded,
            $"Enum_WorkgroupDisposition_{disposition}", info, $"{document.Title}: {note.Trim()}", ct);
        // The coordinators owe the follow-up, so they also get the email.
        await notices.EmailAsync(info.CoordinatorUserIds(), WorkgroupNoticeKind.DispositionRecorded, info,
            $"{disposition}: {note.Trim()}", ct);
        return new();
    }

    private async Task AnnounceDecisionAsync(
        Workgroup workgroup,
        WorkgroupNoticeKind kind,
        string? detail,
        CancellationToken ct)
    {
        var info = ToInfo(workgroup);
        var coordinators = info.CoordinatorUserIds();
        await notices.NotifyAsync(coordinators, WorkgroupsNotificationSources.WorkgroupRegistrationDecided, $"Enum_WorkgroupLogKind_{kind}", info, detail, ct);
        await notices.EmailAsync(coordinators, kind, info, detail, ct);
    }
}
