using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

/// <summary>
/// The Secretary's and the Board's decisions (design §6): register, refer, refuse,
/// withdraw, close, reactivate, register an existing group, and record the Board's reply
/// to a delivered document. Registration is administrative recognition only — nothing
/// here happens without a human asking for it.
/// </summary>
internal sealed partial class WorkgroupService
{
    public async Task<WorkgroupMutationResult> RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        // Registration completes even if the initiating request disconnects.
        ct = CancellationToken.None;
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred) is { } statusRefusal) return new(Refusal: statusRefusal);

        // The folder comes first: a group is not registered until it has somewhere to work,
        // and a failure here leaves the row exactly as it was so the Secretary can retry.
        if (workgroup.DriveFolderId is null)
        {
            var folderResult = await CreateGroupFolderAsync(workgroup, ct);
            if (folderResult.Refusal is { } folderRefusal) return new(Refusal: folderRefusal);
            workgroup.DriveFolderId = folderResult.Value!;
        }

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Active;
        workgroup.RegisteredAt = now;
        workgroup.Reasons = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Registered, now, body: null, ct);
        await AuditAsync(AuditAction.WorkgroupRegistered, workgroup,
            $"Registered '{workgroup.Name}' with Drive folder {workgroup.DriveFolderId}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Registered, detail: null, ct);
        await RequestDriveSyncAsync(workgroup, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ReferAsync(
        Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Referred;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        // The decision committed; finish its record and notices after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Referred, now, Trimmed(note), ct);
        await AuditAsync(AuditAction.WorkgroupReferred, workgroup,
            $"Referred '{workgroup.Name}' to the Board{(Trimmed(note) is { } n ? $": {n}" : "")}",
            actorUserId);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(NotificationSource.WorkgroupRegistrationPending,
            $"Referred to the Board: {workgroup.Name}", info,
            Trimmed(note) ?? "A refusal ground may apply; the Board decides at its next meeting.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Referred, info, Trimmed(note), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> RefuseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Refused;
        workgroup.Reasons = reasons.Trim();
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        // The decision committed; finish its record and notices after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Refused, now, reasons.Trim(), ct);
        await AuditAsync(AuditAction.WorkgroupRefused, workgroup,
            $"Refused '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Refused, reasons.Trim(), ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> WithdrawAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Withdrawn;
        workgroup.DormantReason = null;
        workgroup.Reasons = reasons.Trim();
        workgroup.EndedAt = now;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        // The decision committed; finish its record and notices after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Withdrawn, now, reasons.Trim(), ct);
        await AuditAsync(AuditAction.WorkgroupWithdrawn, workgroup,
            $"Withdrew registration of '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Withdrawn, reasons.Trim(), ct);
        // Withdrawn is not Active, so the source stops claiming write access.
        await RequestDriveSyncAsync(workgroup, ct);
        await SetAccountActiveAsync(workgroup, isActive: false, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> CloseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        if (RequireReasons(reasons) is { } reasonsRefusal) return new(Refusal: reasonsRefusal);
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Active) is { } statusRefusal) return new(Refusal: statusRefusal);

        // The Secretary's close after the two-month silence; the member's own ending is
        // MarkDoneAsync, which cannot reach the Quiet reason.
        await EndAsync(workgroup, actorUserId, WorkgroupDormantReason.Quiet, reasons, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        var workgroupResult = await RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Active;
        workgroup.DormantReason = null;
        workgroup.EndedAt = null;
        workgroup.Reasons = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);
        // The decision committed; finish its record and notices after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Reactivated, now, body: null, ct);
        await AuditAsync(AuditAction.WorkgroupReactivated, workgroup,
            $"Reactivated '{workgroup.Name}'", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Reactivated, detail: null, ct);
        // Active again: the source claims ContentManager for the current members once more.
        await RequestDriveSyncAsync(workgroup, ct);
        await SetAccountActiveAsync(workgroup, isActive: true, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> RegisterExistingAsync(
        Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (ValidateApplication(bootstrap.Application) is { } applicationRefusal) return new(Refusal: applicationRefusal);

        var now = clock.GetCurrentInstant();
        var slugResult = await ReserveSlugAsync(bootstrap.Application.Name, null, ct);
        if (slugResult.Refusal is { } slugRefusal) return new(Refusal: slugRefusal);
        var slug = slugResult.Value!;
        var workgroup = new Workgroup
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            // Bootstrapping records a group that already exists: it is registered from the
            // moment it really started, and the application it never filed is the same date.
            Status = WorkgroupStatus.Active,
            AppliedByUserId = bootstrap.CoordinatorUserId,
            AppliedAt = bootstrap.RegisteredAt,
            RegisteredAt = bootstrap.RegisteredAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        var application = bootstrap.Application;
        ApplyFields(workgroup, application.Name, application.Purpose, application.Deliverable,
            application.DeliverableKind, application.Audience, application.TargetDate,
            application.DiscordChannelUrl);

        workgroup.Members.Add(NewMember(workgroup.Id, bootstrap.CoordinatorUserId,
            WorkgroupMemberRole.Coordinator, bootstrap.RegisteredAt));
        if (application.SecondCoordinatorUserId is { } second && second != bootstrap.CoordinatorUserId)
        {
            workgroup.Members.Add(NewMember(workgroup.Id, second,
                WorkgroupMemberRole.Coordinator, bootstrap.RegisteredAt));
        }

        workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.Registered,
            bootstrap.RegisteredAt, body: null));

        // Finance before anything durable: a Holded failure must fail the whole registration,
        // or the form reports an error for a group that exists and a retry registers it twice.
        var budget = bootstrap.Budget is { Amount: not null } b ? b : null;
        if (budget is not null)
        {
            if (budget.Amount < 0)
                return new(Refusal: new(WorkgroupErrorKeys.BudgetNegative));
            var accountResult = await ResolveBudgetAccountAsync(workgroup, budget.ExistingAccountNum, ct);
            if (accountResult.Refusal is { } accountRefusal) return new(Refusal: accountRefusal);
            var account = accountResult.Value!;
            workgroup.BudgetAmount = budget.Amount;
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
            workgroup.LogEntries.Add(SystemEntry(workgroup.Id, WorkgroupLogKind.BudgetSet, now,
                BudgetLogBody(workgroup), actorUserId));
        }

        var folderResult = await CreateGroupFolderAsync(workgroup, ct);
        if (folderResult.Refusal is { } folderRefusal) return new(Refusal: folderRefusal);
        workgroup.DriveFolderId = folderResult.Value!;
        await repository.AddWorkgroupAsync(workgroup, ct);

        await AuditAsync(AuditAction.WorkgroupRegisteredExisting, workgroup,
            $"Registered the existing group '{workgroup.Name}', backdated to "
                + $"{bootstrap.RegisteredAt.InUtc().Date.ToInvariantDate()}",
            actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Registered, detail: null, ct);
        await RequestDriveSyncAsync(workgroup, ct);
        if (budget is not null)
            await AuditAsync(AuditAction.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);

        return new(workgroup.Id);
    }

    public async Task<WorkgroupMutationResult> RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
            return new(Refusal: new(WorkgroupErrorKeys.ReasonsRequired));

        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        // The Board replies to what it received, so there is nothing to dispose of until
        // the group has delivered.
        if (document.Status != WorkgroupDocumentStatus.Delivered)
            return new(Refusal: new(WorkgroupErrorKeys.NotDelivered));

        var now = clock.GetCurrentInstant();
        document.Disposition = disposition;
        document.DispositionNote = note.Trim();
        document.DispositionAt = now;
        document.DispositionByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);
        // The decision committed; finish its record and notices after browser cancellation.
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.DispositionRecorded, now,
            $"{disposition}: {note.Trim()}", ct, documentId: document.Id);
        await AuditAsync(AuditAction.WorkgroupDispositionRecorded, workgroup,
            $"Recorded {disposition} on '{document.Title}': {note.Trim()}", actorUserId,
            AuditEntityTypes.WorkgroupDocument, document.Id);

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), NotificationSource.WorkgroupDispositionRecorded,
            $"Enum_WorkgroupDisposition_{disposition}", info, $"{document.Title}: {note.Trim()}", ct);
        // Members see it in-app; the coordinators, who owe the follow-up, also get the email.
        await EmailAsync(info.CoordinatorUserIds(), WorkgroupNoticeKind.DispositionRecorded, info,
            $"{disposition}: {note.Trim()}", ct);
        return new();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the group's subfolder under the configured root. Both failure modes are
    /// the Secretary's to act on: an unset root is a settings problem, a Google failure is
    /// a retry — neither leaves the group half-registered.
    /// </summary>
    private async Task<WorkgroupMutationResult<string>> CreateGroupFolderAsync(Workgroup workgroup, CancellationToken ct)
    {
        var root = await GetRootDriveFolderIdAsync(ct);
        if (string.IsNullOrWhiteSpace(root))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        try
        {
            // The entire registration uses a non-cancellable token.
            return new(await googleSync.CreateSubfolderAsync(root, workgroup.Name, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create the Drive subfolder for workgroup {WorkgroupId}",
                workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.DriveFolderCreationFailed));
        }
    }

    /// <summary>The §14 pair for a decision the coordinators need to know about: notify and email.</summary>
    private async Task AnnounceDecisionAsync(
        Workgroup workgroup,
        WorkgroupNoticeKind kind,
        string? detail,
        CancellationToken ct)
    {
        var info = ToInfo(workgroup);
        var coordinators = info.CoordinatorUserIds();
        await NotifyAsync(coordinators, NotificationSource.WorkgroupRegistrationDecided, $"Enum_WorkgroupLogKind_{kind}", info, detail, ct);
        await EmailAsync(coordinators, kind, info, detail, ct);
    }
}
