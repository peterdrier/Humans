using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using NodaTime;
using static Humans.Workgroups.Services.WorkgroupAccess;
using static Humans.Workgroups.Services.WorkgroupJournal;
using static Humans.Workgroups.Services.WorkgroupRegisterReader;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupMembership(
    IWorkgroupRepository repository,
    WorkgroupAccess access,
    WorkgroupJournal journal,
    WorkgroupNotices notices,
    WorkgroupGoogleResources googleResources,
    IUserService users,
    IAuditLogService auditLog,
    IClock clock) : IApplicationService
{
    internal async Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default)
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

    internal async Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        var pending = workgroup.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt is null && m.IsPending == true);
        if (pending is null) return new(Refusal: new(WorkgroupErrorKeys.NoPendingRequest));

        return await AdmitAsync(workgroup, pending, actorUserId, WorkgroupsAuditActions.WorkgroupMembershipApproved,
            "Workgroups_Notice_MembershipApproved", ct);
    }

    internal async Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
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

    internal async Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var workgroupResult = await access.RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        var target = await access.ResolveMemberTargetAsync(userId, ct);
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
        await googleResources.RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    internal async Task<WorkgroupMutationResult> LeaveAsync(
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

        await googleResources.RequestGoogleSyncAsync(workgroup, ct);
        return new();
    }

    internal async Task<WorkgroupMutationResult> SetCoordinatorsAsync(
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

    internal static WorkgroupMember NewMember(
        Guid workgroupId, Guid userId, WorkgroupMemberRole role, Instant now, bool isPending = false) => new()
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            UserId = userId,
            Role = role,
            JoinedAt = now,
            IsPending = isPending ? true : null
        };

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

}
