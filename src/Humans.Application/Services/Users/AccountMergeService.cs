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
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Users;

// AcceptAsync fans out IUserMerge across sections to re-FK source→target, then tombstones source via AnonymizeForMergeAsync.
public sealed class AccountMergeService(
    IAccountMergeRepository mergeRepository,
    IUserRepository userRepository,
    IAuditLogService auditLogService,
    IUserInfoInvalidator userInfoInvalidator,
    ILogger<AccountMergeService> logger,
    IClock clock,
    IEnumerable<IUserMerge> userMerges,
    IUserService userService,
    IActiveTeamsCacheInvalidator activeTeamsCacheInvalidator,
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
        Guid requestId, Guid adminUserId, Guid survivorUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdPlainAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");
        if (request.Status != AccountMergeRequestStatus.Pending)
            throw new InvalidOperationException("Merge request is not pending.");
        if (survivorUserId != request.TargetUserId && survivorUserId != request.SourceUserId)
            throw new InvalidOperationException("Survivor must be one of the request's two accounts.");

        var archivedUserId = survivorUserId == request.TargetUserId
            ? request.SourceUserId : request.TargetUserId;

        // Only verify the request's pending email when its owner (the request target) is
        // the survivor. If the admin flipped direction the target is being archived, so its
        // pending email moves to the survivor via the fan-out instead of being verified here.
        var pendingEmailToVerify = survivorUserId == request.TargetUserId
            ? request.PendingEmailId : (Guid?)null;

        // MergeAsync.CloseRequestsForPairAsync closes this request (and any siblings) as Accepted.
        await MergeAsync(survivorUserId, archivedUserId, adminUserId, notes, pendingEmailToVerify, ct);
    }

    public async Task MergeAsync(
        Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
        string? notes = null, Guid? pendingEmailIdToVerify = null,
        CancellationToken ct = default)
    {
        if (survivorUserId == archivedUserId)
            throw new InvalidOperationException("Survivor and archived users are the same.");

        var survivor = await userService.GetUserInfoAsync(survivorUserId, ct)
            ?? throw new InvalidOperationException($"Survivor user {survivorUserId} not found.");
        var archived = await userService.GetUserInfoAsync(archivedUserId, ct)
            ?? throw new InvalidOperationException($"Archived user {archivedUserId} not found.");
        if (survivor.IsMerged)
            throw new InvalidOperationException($"Survivor user {survivorUserId} is already tombstoned.");
        if (archived.IsMerged)
            throw new InvalidOperationException(
                $"Archived user {archivedUserId} is already tombstoned (merged into {archived.MergedToUserId}).");

        var now = clock.GetCurrentInstant();

        logger.LogInformation(
            "Admin {AdminId} merging: folding archived {ArchivedId} into survivor {SurvivorId}",
            adminUserId, archivedUserId, survivorUserId);

        try
        {
            // 1. Move every section's user-keyed rows archived -> survivor.
            //    Ordered/sequential; each commits independently; re-FK of already-moved
            //    rows is a no-op, so the whole step is safely retryable.
            foreach (var merger in userMerges)
                await merger.ReassignAsync(archivedUserId, survivorUserId, adminUserId, now, ct);

            // 2. Settle the pending email (the gmail-normalized-but-not-identical case the
            //    row-collapse missed). NON-FATAL: a missing/already-consumed pending email
            //    is the desired end state, not an error — so the bool result is ignored.
            if (pendingEmailIdToVerify is Guid pendingId)
                await userRepository.MarkUserEmailVerifiedAsync(pendingId, now, ct);

            // 3. Tombstone the archived account LAST — the observable commit point and
            //    source of truth (no wipe — chain-follow reads need the redirect).
            await userService.AnonymizeForMergeAsync(archivedUserId, survivorUserId, now, ct);

            // 4. Audit.
            await auditLogService.LogAsync(
                AuditAction.AccountMergeAccepted,
                nameof(User), archivedUserId,
                $"Folded archived {archivedUserId} into survivor {survivorUserId}. Notes: {notes ?? "(none)"}",
                adminUserId,
                relatedEntityId: survivorUserId, relatedEntityType: nameof(User));

            // 5. Best-effort: close pending merge requests for this pair. If this throws,
            //    the tombstone (step 3) already makes them self-reconcilable on the
            //    listing page — there is no half-merge to recover from.
            await CloseRequestsForPairAsync(survivorUserId, archivedUserId, adminUserId, now, notes, ct);
        }
        finally
        {
            InvalidateMergeCaches(survivorUserId, archivedUserId);
        }
    }

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

    // Closes every pending merge request for the survivor/archived pair as Accepted.
    // The pending set is tiny at this scale, so we filter GetPendingAsync in memory
    // rather than add a pair-scoped repository query (reuse-first).
    private async Task CloseRequestsForPairAsync(
        Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
        Instant now, string? notes, CancellationToken ct)
    {
        var pending = await mergeRepository.GetPendingAsync(ct);
        var pair = new HashSet<Guid> { survivorUserId, archivedUserId };
        foreach (var req in pending.Where(r => pair.Contains(r.SourceUserId) && pair.Contains(r.TargetUserId)))
        {
            req.Status = AccountMergeRequestStatus.Accepted;
            req.ResolvedAt = now;
            req.ResolvedByUserId = adminUserId;
            req.AdminNotes = notes ?? "Resolved by merge.";
            await mergeRepository.UpdateAsync(req, ct);
        }
    }

    // Cache-aside eviction after a merge. Runs in MergeAsync's finally so a partial/failed
    // run still clears stale per-user reads.
    private void InvalidateMergeCaches(Guid survivorUserId, Guid archivedUserId)
    {
        roleAssignmentService.InvalidateClaimsCacheForUser(archivedUserId);
        roleAssignmentService.InvalidateClaimsCacheForUser(survivorUserId);
        roleAssignmentService.InvalidateNavBadgeCache();
        roleAssignmentService.InvalidateRoleAssignmentCache();
        notificationService.InvalidateBadgeCachesForUsers([archivedUserId, survivorUserId]);
        consentCacheInvalidator.InvalidateUser(archivedUserId);
        consentCacheInvalidator.InvalidateUser(survivorUserId);
        activeTeamsCacheInvalidator.Invalidate();
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
            await userRepository.RemoveUserEmailByIdAsync(request.PendingEmailId, ct);

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

    public async Task ReconcileMergedRequestAsync(
        Guid requestId, Guid adminUserId, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdPlainAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");
        if (request.Status != AccountMergeRequestStatus.Pending)
            throw new InvalidOperationException("Merge request is not pending.");

        var source = await userService.GetUserInfoAsync(request.SourceUserId, ct);
        var target = await userService.GetUserInfoAsync(request.TargetUserId, ct);
        // Only close when this pair actually merged INTO EACH OTHER (one tombstoned into
        // the other). A side merged into some unrelated third account is a different
        // conflict — closing here would silently drop the still-unresolved pending email.
        var mergedIntoEachOther =
            source?.MergedToUserId == request.TargetUserId ||
            target?.MergedToUserId == request.SourceUserId;
        if (!mergedIntoEachOther)
            throw new InvalidOperationException(
                "These two accounts are not merged into each other — resolve this request via Merge or Dismiss instead.");

        // The merge already happened; just close the orphaned request. No data/email
        // mutation — reuses the same close path the engine runs after a live merge.
        await CloseRequestsForPairAsync(
            request.TargetUserId, request.SourceUserId, adminUserId,
            clock.GetCurrentInstant(), "Reconciled: accounts already merged.", ct);

        await auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(AccountMergeRequest), request.Id,
            $"Reconciled orphan merge request — accounts already merged (source {request.SourceUserId}, target {request.TargetUserId}).",
            adminUserId);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await mergeRepository.GetForUserGdprAsync(userId, ct);

        var shaped = rows.Select(r => new
        {
            r.Status,
            Role = r.IsTarget ? "Target" : "Source",
            CreatedAt = r.CreatedAt.ToIso8601(),
            ResolvedAt = r.ResolvedAt.ToIso8601()
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
}
