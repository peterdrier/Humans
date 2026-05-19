using System.Transactions;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Profiles;

// AcceptAsync fans out IUserMerge across sections to re-FK source→target, then tombstones source via AnonymizeForMergeAsync.
public sealed class AccountMergeService(
    IAccountMergeRepository mergeRepository,
    IUserEmailRepository userEmailRepository,
    IAuditLogService auditLogService,
    IUserInfoInvalidator userInfoInvalidator,
    ILogger<AccountMergeService> logger,
    IClock clock,
    IEnumerable<IUserMerge> userMerges,
    IUserService userService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService,
    INotificationService notificationService,
    IConsentCacheInvalidator consentCacheInvalidator) : IAccountMergeService, IUserDataContributor
{
    // Fan-out — IUserMerge implementations register in each section's Add…Section extension.

    public async Task<IReadOnlyList<AccountMergeRequestSnapshot>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        var requests = await mergeRepository.GetPendingAsync(ct);
        if (requests.Count == 0) return [];

        var userIds = CollectUserIds(requests);
        var users = await userService.GetUserInfosAsync(userIds, ct);
        return requests.Select(r => ToSnapshot(r, users)).ToList();
    }

    public async Task<AccountMergeRequestSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdAsync(id, ct);
        if (request is null) return null;

        var userIds = CollectUserIds([request]);
        var users = await userService.GetUserInfosAsync(userIds, ct);
        return ToSnapshot(request, users);
    }

    private static IReadOnlyCollection<Guid> CollectUserIds(IReadOnlyList<AccountMergeRequest> requests)
    {
        var ids = new HashSet<Guid>();
        foreach (var r in requests)
        {
            ids.Add(r.TargetUserId);
            ids.Add(r.SourceUserId);
            if (r.ResolvedByUserId is Guid resolvedBy) ids.Add(resolvedBy);
        }
        return ids;
    }

    public async Task AcceptAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");
        if (request.Status != AccountMergeRequestStatus.Pending)
            throw new InvalidOperationException("Merge request is not pending.");

        logger.LogInformation(
            "Admin {AdminId} accepting merge request {RequestId}: folding {SourceUserId} into {TargetUserId}",
            adminUserId, requestId, request.SourceUserId, request.TargetUserId);

        var audit = new AuditEntry(
            AuditAction.AccountMergeAccepted,
            nameof(AccountMergeRequest), request.Id,
            $"Folded source {request.SourceUserId} into target {request.TargetUserId} — email: {request.Email}",
            RelatedEntityId: request.TargetUserId,
            RelatedEntityType: nameof(User));

        await FoldAsync(request.SourceUserId, request.TargetUserId, adminUserId, audit, ct);

        var now = clock.GetCurrentInstant();
        using var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions
            {
                IsolationLevel = IsolationLevel.ReadCommitted
            },
            TransactionScopeAsyncFlowOption.Enabled);
        var verified = await userEmailRepository.MarkVerifiedAsync(request.PendingEmailId, now, ct);
        if (!verified)
            throw new InvalidOperationException(
                $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");

        request.Status = AccountMergeRequestStatus.Accepted;
        request.ResolvedAt = now;
        request.ResolvedByUserId = adminUserId;
        request.AdminNotes = notes;
        await mergeRepository.UpdateAsync(request, ct);

        scope.Complete();
    }

    private sealed record AuditEntry(
        AuditAction Action,
        string EntityType,
        Guid EntityId,
        string Description,
        Guid? RelatedEntityId = null,
        string? RelatedEntityType = null);

    private static AccountMergeRequestSnapshot ToSnapshot(
        AccountMergeRequest request,
        IReadOnlyDictionary<Guid, UserInfo> users) =>
        new(
            request.Id,
            request.Email,
            ToUserSnapshot(request.TargetUserId, users),
            ToUserSnapshot(request.SourceUserId, users),
            request.Status,
            request.CreatedAt,
            request.ResolvedAt,
            request.ResolvedByUserId is Guid id && users.TryGetValue(id, out var rb)
                ? rb.BurnerName
                : null,
            request.AdminNotes);

    private static AccountMergeUserSnapshot ToUserSnapshot(
        Guid userId,
        IReadOnlyDictionary<Guid, UserInfo> users)
    {
        if (users.TryGetValue(userId, out var user))
        {
            return new(
                user.Id,
                user.BurnerName,
                user.Email,
                user.ProfilePictureUrl,
                user.PreferredLanguage,
                user.LastLoginAt);
        }
        // Missing user → stub for snapshot record's non-null contract.
        return new(userId, "(unknown user)", null, null, null, null);
    }

    private async Task FoldAsync(
        Guid sourceUserId, Guid targetUserId,
        Guid adminUserId, AuditEntry audit,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        try
        {
            // Ambient transaction — section services use IDbContextFactory; Npgsql enlists. AsyncFlow required for await.
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions
                {
                    IsolationLevel = IsolationLevel.ReadCommitted
                },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                // Fan-out: each IUserMerge re-FKs source→target. Order is irrelevant inside the transaction.
                foreach (var merger in userMerges)
                    await merger.ReassignAsync(sourceUserId, targetUserId, adminUserId, now, ct);

                // Tombstone source User (no wipe — chain-follow reads need the redirect).
                await userService.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);

                // Audit inside scope — rolled-back fold must not leave a ghost row.
                await auditLogService.LogAsync(
                    audit.Action,
                    audit.EntityType, audit.EntityId,
                    audit.Description,
                    adminUserId,
                    relatedEntityId: audit.RelatedEntityId,
                    relatedEntityType: audit.RelatedEntityType);

                scope.Complete();
            }

            // Cache invalidation AFTER commit so cache-aside readers don't repopulate from rolled-back rows.
            teamService.RemoveMemberFromAllTeamsCache(sourceUserId);
            roleAssignmentService.InvalidateClaimsCacheForUser(sourceUserId);
            roleAssignmentService.InvalidateClaimsCacheForUser(targetUserId);
            roleAssignmentService.InvalidateNavBadgeCache();
            roleAssignmentService.InvalidateRoleAssignmentCache();
            notificationService.InvalidateBadgeCachesForUsers([sourceUserId, targetUserId]);

            // T-04: rebuild target's UserConsentInfo against post-merge chain (§12 — source records stay at source).
            consentCacheInvalidator.InvalidateUser(sourceUserId);
            consentCacheInvalidator.InvalidateUser(targetUserId);
        }
        finally
        {
            // Evict ActiveTeams cache: TeamService mutates it during scope; rolled-back state would otherwise leak.
            teamService.InvalidateActiveTeamsCache();
        }
    }

    public async Task RejectAsync(
        Guid requestId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdPlainAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");

        if (request.Status != AccountMergeRequestStatus.Pending)
        {
            throw new InvalidOperationException("Merge request is not pending.");
        }

        var now = clock.GetCurrentInstant();

        // Transaction so pending-email delete and request status commit together (else dangling PendingEmailId blocks future Accept).
        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            // Best-effort remove the target's pending email.
            await userEmailRepository.RemoveByIdAsync(request.PendingEmailId, ct);

            request.Status = AccountMergeRequestStatus.Rejected;
            request.ResolvedAt = now;
            request.ResolvedByUserId = adminUserId;
            request.AdminNotes = notes;
            await mergeRepository.UpdateAsync(request, ct);

            await auditLogService.LogAsync(
                AuditAction.AccountMergeRejected,
                nameof(AccountMergeRequest), request.Id,
                $"Rejected merge request for email {request.Email} (target: {request.TargetUserId}, source: {request.SourceUserId})",
                adminUserId);

            scope.Complete();
        }

        // Invalidate target's UserInfo AFTER commit.
        await userInfoInvalidator.InvalidateAsync(request.TargetUserId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await mergeRepository.GetForUserGdprAsync(userId, ct);

        var shaped = rows.Select(r => new
        {
            Status = r.Status,
            Role = r.IsTarget ? "Target" : "Source",
            CreatedAt = r.CreatedAt.ToInvariantInstantString(),
            ResolvedAt = r.ResolvedAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.AccountMergeRequests, shaped)];
    }

    // --- Cross-section read helpers for UserEmailService ---

    public Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default) =>
        mergeRepository.GetPendingEmailIdsAsync(emailIds, ct);

    public Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        mergeRepository.HasPendingForUserAndEmailAsync(
            targetUserId, normalizedEmail, alternateEmail, ct);

    public Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default) =>
        mergeRepository.HasPendingForEmailIdAsync(pendingEmailId, ct);

    public Task CreateAsync(AccountMergeRequest request, CancellationToken ct = default) =>
        mergeRepository.AddAsync(request, ct);

    public async Task AdminMergeAsync(
        Guid sourceUserId, Guid targetUserId,
        Guid adminUserId, string? notes = null,
        CancellationToken ct = default)
    {
        if (sourceUserId == targetUserId)
            throw new InvalidOperationException("Source and target users are the same.");

        var source = await userService.GetUserInfoAsync(sourceUserId, ct)
            ?? throw new InvalidOperationException($"Source user {sourceUserId} not found.");
        var target = await userService.GetUserInfoAsync(targetUserId, ct)
            ?? throw new InvalidOperationException($"Target user {targetUserId} not found.");

        if (source.MergedToUserId is not null)
            throw new InvalidOperationException(
                $"Source user {sourceUserId} is already tombstoned (merged into {source.MergedToUserId}).");

        if (target.MergedToUserId is not null)
            throw new InvalidOperationException(
                $"Target user {targetUserId} is already tombstoned.");

        logger.LogInformation(
            "Admin {AdminId} initiated direct merge: folding {SourceUserId} into {TargetUserId}",
            adminUserId, sourceUserId, targetUserId);

        var description = $"Admin-initiated via EmailProblems: folded source {sourceUserId} into target {targetUserId}. Notes: {notes ?? "(none)"}";

        var audit = new AuditEntry(
            AuditAction.AccountMergeAccepted,
            nameof(User), sourceUserId,
            description,
            RelatedEntityId: targetUserId,
            RelatedEntityType: nameof(User));

        await FoldAsync(sourceUserId, targetUserId, adminUserId, audit, ct);
    }
}
