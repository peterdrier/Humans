using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampMembershipTransitions(
    ICampRepository repo,
    IAuditLogService auditLog,
    IUserService userService,
    ISettingsService settingsService,
    ITicketService tickets,
    Lazy<ICampRoleService> campRoleService,
    CampMembershipNotices membershipNotices,
    IClock clock) : IApplicationService
{
    /// <summary>Sole CampMember→Removed transition: role-cascade, state flip, audit. Callers own preconditions and post-effects.</summary>
    private async Task TransitionMemberToRemovedAsync(
        CampMember member,
        Guid actorUserId,
        AuditActionDefinition auditAction,
        string auditMessage,
        bool cascadeRoleAssignments,
        CancellationToken cancellationToken)
    {
        // A grant whose holder already entered the event is consumed: keep the
        // flag on the Removed row so the slot-cap count still includes it —
        // otherwise removing the member frees the slot for a second entry.
        var retainConsumedEeGrant = member.HasEarlyEntry
            && await HasEnteredEventAsync(member.UserId, member.CampSeason.Year, cancellationToken);

        var now = clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = actorUserId;
        member.HasEarlyEntry = retainConsumedEeGrant;
        var removedAssignments = await repo.SaveMemberAsync(member, cancellationToken, cascadeRoleAssignments);

        if (removedAssignments > 0)
        {
            await auditLog.LogAsync(
                CampsAuditActions.CampRoleUnassigned, nameof(CampMember), member.Id,
                $"Cascade-removed {removedAssignments} role assignment(s) for camp member.", actorUserId);
        }

        await auditLog.LogAsync(
            auditAction, nameof(CampMember), member.Id,
            auditMessage, actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));
    }

    public async Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var activeEventId = (settingsService.GetActiveEventSettings())?.Id;
        var camp = await repo.GetByIdAsync(campId, cancellationToken);
        // Active AND Full both accept requests — Full is an informational label the lead
        // sets to say "we look full," not an enforcement gate. Humans doesn't yet know
        // everyone actually in the camp, so people still need to be able to request/join.
        var season = camp?.Seasons.FirstOrDefault(s =>
            activeEventId is not null
            && s.EventSettingsId == activeEventId
            && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full));
        if (season is null)
        {
            return new CampMemberRequestResult(
                Guid.Empty,
                CampMemberRequestOutcome.NoOpenSeason,
                "Camps_Flash_RequestNoOpenSeason",
                CampMemberRequestNoticeLevel.Error);
        }

        var now = clock.GetCurrentInstant();
        var insert = await repo.RequestMembershipAsync(season.Id, userId, now, cancellationToken);

        if (insert.Outcome == CampMemberInsertOutcome.Created)
        {
            await auditLog.LogAsync(
                CampsAuditActions.CampMemberRequested, nameof(CampMember), insert.MemberId,
                $"Requested membership in camp season {season.Year}",
                userId,
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
        }

        return insert.Outcome switch
        {
            CampMemberInsertOutcome.Created =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.Created,
                    "Camps_Flash_RequestCreated",
                    CampMemberRequestNoticeLevel.Success),
            CampMemberInsertOutcome.AlreadyActive =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyActive,
                    "Camps_Flash_RequestAlreadyActive",
                    CampMemberRequestNoticeLevel.Info),
            _ =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyPending,
                    "Camps_Flash_RequestAlreadyPending",
                    CampMemberRequestNoticeLevel.Info)
        };
    }

    public async Task<CampMembershipMutationResult> ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_ApproveRequiresPending");
        }

        var now = clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Active;
        member.ConfirmedAt = now;
        member.ConfirmedByUserId = approvedByUserId;
        await repo.SaveMemberAsync(member, cancellationToken);

        await auditLog.LogAsync(
            CampsAuditActions.CampMemberApproved, nameof(CampMember), member.Id,
            $"Approved camp membership for season {member.CampSeason.Year}",
            approvedByUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        await membershipNotices.NotifyDecisionAsync(scopedCampId, member.CampSeasonId, member.UserId, member.Id, approved: true);
        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
            return CampMembershipMutationResult.Failure("Camps_Flash_RejectRequiresPending");

        var requesterUserId = member.UserId;
        var seasonId = member.CampSeasonId;

        await TransitionMemberToRemovedAsync(
            member, rejectedByUserId,
            CampsAuditActions.CampMemberRejected,
            $"Rejected camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: false,
            cancellationToken);

        await membershipNotices.NotifyDecisionAsync(scopedCampId, seasonId, requesterUserId, member.Id, approved: false);
        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Active)
            return CampMembershipMutationResult.Failure("Camps_Flash_RemoveRequiresActive");

        await TransitionMemberToRemovedAsync(
            member, removedByUserId,
            CampsAuditActions.CampMemberRemoved,
            $"Removed camp member from season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);
        return CampMembershipMutationResult.Success();

    }

    private async Task<Guid?> EnsureActiveCampMemberAsync(Guid campSeasonId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (user is null || user.IsTombstone)
            return null;
        // Merge aliases identify the survivor; never create a new footprint on the archived account.
        userId = user.Id;
        var now = clock.GetCurrentInstant();
        var result = await repo.AddActiveMembershipAsync(campSeasonId, userId, now, actorUserId, cancellationToken);

        if (result.Outcome != CampMemberInsertOutcome.AlreadyActive)
        {
            await auditLog.LogAsync(
                CampsAuditActions.CampMemberAddedByLead,
                nameof(CampMember), result.MemberId,
                "Lead added human as active camp member.",
                actorUserId,
                relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
        }

        return result.MemberId;
    }

    /// <summary>The camp's Active or Full season for the event (the active event when null).</summary>
    private async Task<CampSeason?> FindOpenSeasonAsync(
        Guid campId, Guid? eventSettingsId, CancellationToken cancellationToken)
    {
        var targetEventId = eventSettingsId
            ?? (settingsService.GetActiveEventSettings())?.Id;
        if (targetEventId is null)
            return null;
        var camp = await repo.GetByIdAsync(campId, cancellationToken);
        // Full is informational only: it must not block camp management.
        return camp?.Seasons.FirstOrDefault(s => s.EventSettingsId == targetEventId
            && s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full);
    }

    public async Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, Guid? eventSettingsId = null)
    {
        if (userId == Guid.Empty)
            return AddCampMemberOutcome.InvalidUser;

        var openSeason = await FindOpenSeasonAsync(campId, eventSettingsId, cancellationToken);
        if (openSeason is null)
            return AddCampMemberOutcome.NoActiveSeason;

        var memberId = await EnsureActiveCampMemberAsync(openSeason.Id, userId, actorUserId, cancellationToken);
        return memberId is null ? AddCampMemberOutcome.InvalidUser : AddCampMemberOutcome.Added;
    }

    public async Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, Guid? eventSettingsId = null)
    {
        var openSeason = await FindOpenSeasonAsync(campId, eventSettingsId, cancellationToken);
        if (openSeason is null)
            return AssignCampRoleOutcome.SeasonNotFound;

        var user = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (user is null || user.IsTombstone)
            return AssignCampRoleOutcome.MemberNotFound;
        return await campRoleService.Value.AssignAsync(
            openSeason.Id, roleDefinitionId, Guid.Empty, actorUserId, cancellationToken, addUserId: userId);
    }

    public async Task<CampMembershipMutationResult> WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
            return CampMembershipMutationResult.Failure("Camps_Flash_WithdrawRequiresPending");

        await TransitionMemberToRemovedAsync(
            member, userId,
            CampsAuditActions.CampMemberWithdrawn,
            $"Withdrew camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken);
        if (member is null)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");
        }

        if (member.Status != CampMemberStatus.Active)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_LeaveRequiresActive");
        }

        await TransitionMemberToRemovedAsync(
            member, userId,
            CampsAuditActions.CampMemberLeft,
            $"Left camp season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        return CampMembershipMutationResult.Success();
    }

    public async Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return SetEarlyEntryOutcome.MemberNotFound;

        if (member.HasEarlyEntry == granted)
            return SetEarlyEntryOutcome.NoChange;

        if (granted)
        {
            if (member.Status != CampMemberStatus.Active)
                return SetEarlyEntryOutcome.MemberNotActive;

            var current = await repo.GetGrantedCountForSeasonAsync(member.CampSeasonId, cancellationToken);
            if (current >= member.CampSeason.EeSlotCount)
                return SetEarlyEntryOutcome.SlotCapExceeded;
        }
        else if (await HasEnteredEventAsync(member.UserId, member.CampSeason.Year, cancellationToken))
        {
            // The grant is consumed once the holder is through the gate —
            // revoking it would free the slot for someone else, yielding
            // N+1 early entries from N slots.
            return SetEarlyEntryOutcome.MemberAlreadyEntered;
        }

        member.HasEarlyEntry = granted;
        await repo.SaveMemberAsync(member, cancellationToken);

        await auditLog.LogAsync(
            granted ? CampsAuditActions.CampEarlyEntryGranted : CampsAuditActions.CampEarlyEntryRevoked,
            nameof(CampMember), member.Id,
            granted
                ? $"Granted Early Entry to member in season {member.CampSeason.Year}."
                : $"Revoked Early Entry from member in season {member.CampSeason.Year}.",
            actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        return SetEarlyEntryOutcome.Success;
    }

    /// <summary>An Attended participation row for the season's year (gate check-in via ticket sync).</summary>
    private async ValueTask<bool> HasEnteredEventAsync(Guid userId, int year, CancellationToken ct) =>
        (await tickets.GetParticipationsAsync(userId, ct)).Any(p =>
            p.Year == year && p.Status == ParticipationStatus.Attended);
}
