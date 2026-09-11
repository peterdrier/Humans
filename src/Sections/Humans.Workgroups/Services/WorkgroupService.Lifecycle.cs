using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
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
    public async Task RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        // Registration completes even if the initiating request disconnects.
        ct = CancellationToken.None;
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred);

        // The folder comes first: a group is not registered until it has somewhere to work,
        // and a failure here leaves the row exactly as it was so the Secretary can retry.
        workgroup.DriveFolderId ??= await CreateGroupFolderAsync(workgroup, ct);

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
    }

    public async Task ReferAsync(
        Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Applied);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Referred;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Referred, now, Trimmed(note), ct);
        await AuditAsync(AuditAction.WorkgroupReferred, workgroup,
            $"Referred '{workgroup.Name}' to the Board{(Trimmed(note) is { } n ? $": {n}" : "")}",
            actorUserId);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(NotificationSource.WorkgroupRegistrationPending,
            $"Referred to the Board: {workgroup.Name}", info,
            Trimmed(note) ?? "A refusal ground may apply; the Board decides at its next meeting.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Referred, info, Trimmed(note), ct);
    }

    public async Task RefuseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        RequireReasons(reasons);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Refused;
        workgroup.Reasons = reasons.Trim();
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Refused, now, reasons.Trim(), ct);
        await AuditAsync(AuditAction.WorkgroupRefused, workgroup,
            $"Refused '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Refused, reasons.Trim(), ct);
    }

    public async Task WithdrawAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        RequireReasons(reasons);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Active, WorkgroupStatus.Dormant);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Withdrawn;
        workgroup.DormantReason = null;
        workgroup.Reasons = reasons.Trim();
        workgroup.EndedAt = now;
        workgroup.DormantSince = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Withdrawn, now, reasons.Trim(), ct);
        await AuditAsync(AuditAction.WorkgroupWithdrawn, workgroup,
            $"Withdrew registration of '{workgroup.Name}': {reasons.Trim()}", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Withdrawn, reasons.Trim(), ct);
        // Withdrawn is not Active, so the source stops claiming write access.
        await RequestDriveSyncAsync(workgroup, ct);
    }

    public async Task CloseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default)
    {
        RequireReasons(reasons);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Active);

        // The Secretary's close after the two-month silence; the member's own ending is
        // MarkDoneAsync, which cannot reach the Quiet reason.
        await EndAsync(workgroup, actorUserId, WorkgroupDormantReason.Quiet, reasons, ct);
    }

    public async Task ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default)
    {
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Dormant);

        var now = clock.GetCurrentInstant();
        workgroup.Status = WorkgroupStatus.Active;
        workgroup.DormantReason = null;
        workgroup.EndedAt = null;
        workgroup.DormantSince = null;
        workgroup.Reasons = null;
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Reactivated, now, body: null, ct);
        await AuditAsync(AuditAction.WorkgroupReactivated, workgroup,
            $"Reactivated '{workgroup.Name}'", actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Reactivated, detail: null, ct);
        // Active again: the source claims Contributor for the current members once more.
        await RequestDriveSyncAsync(workgroup, ct);
    }

    public async Task<Guid> RegisterExistingAsync(
        Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default)
    {
        ct = CancellationToken.None;
        ArgumentNullException.ThrowIfNull(bootstrap);
        ValidateApplication(bootstrap.Application);

        var now = clock.GetCurrentInstant();
        var slug = await ReserveSlugAsync(bootstrap.Application.Name, null, ct);
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

        workgroup.DriveFolderId = await CreateGroupFolderAsync(workgroup, ct);
        await repository.AddWorkgroupAsync(workgroup, ct);

        await AuditAsync(AuditAction.WorkgroupRegisteredExisting, workgroup,
            $"Registered the existing group '{workgroup.Name}', backdated to "
                + $"{bootstrap.RegisteredAt.InUtc().Date.ToInvariantDate()}",
            actorUserId);
        await AnnounceDecisionAsync(workgroup, WorkgroupNoticeKind.Registered, detail: null, ct);
        await RequestDriveSyncAsync(workgroup, ct);

        return workgroup.Id;
    }

    public async Task RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.ReasonsRequired);

        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        // The Board replies to what it received, so there is nothing to dispose of until
        // the group has delivered.
        if (document.Status != WorkgroupDocumentStatus.Delivered)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NotDelivered);

        var now = clock.GetCurrentInstant();
        document.Disposition = disposition;
        document.DispositionNote = note.Trim();
        document.DispositionAt = now;
        document.DispositionByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);

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
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the group's subfolder under the configured root. Both failure modes are
    /// the Secretary's to act on: an unset root is a settings problem, a Google failure is
    /// a retry — neither leaves the group half-registered.
    /// </summary>
    private async Task<string> CreateGroupFolderAsync(Workgroup workgroup, CancellationToken ct)
    {
        var root = await settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);
        if (string.IsNullOrWhiteSpace(root))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.RootFolderNotConfigured);

        try
        {
            // The entire registration uses a non-cancellable token.
            return await googleSync.CreateSubfolderAsync(root, workgroup.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create the Drive subfolder for workgroup {WorkgroupId}",
                workgroup.Id);
            throw new WorkgroupRuleException(WorkgroupErrorKeys.DriveFolderCreationFailed);
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
