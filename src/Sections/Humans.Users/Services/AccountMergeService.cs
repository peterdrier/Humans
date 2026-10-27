using System.Transactions;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;

namespace Humans.Users.Services;

internal sealed class AccountMergeService(
    IAccountMergeRepository mergeRepository,
    IUserRepository userRepository,
    IAuditLogService auditLogService,
    IUserInfoSliceRefresher userInfoRefresher,
    ILogger<AccountMergeService> logger,
    IClock clock,
    IEnumerable<IUserMerge> userMerges,
    IUserManagement userService) : IAccountMergeService, IUserDataContributor
{
    internal const string AccountMergeRequests = "AccountMergeRequests";

    public async Task<IReadOnlyList<AccountMergeRequestSnapshot>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        var requests = await mergeRepository.GetPendingAsync(ct);
        if (requests.Count == 0) return [];

        var userIds = CollectUserIds(requests);
        var users = await GetRawUserInfosAsync(userIds, ct);
        return requests.Select(r => ToSnapshot(r, users)).ToList();
    }

    public async Task<AccountMergeRequestSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdAsync(id, ct);
        if (request is null) return null;

        var userIds = CollectUserIds([request]);
        var users = await GetRawUserInfosAsync(userIds, ct);
        return ToSnapshot(request, users);
    }

    /// <summary>
    /// Raw rows: merge code needs a tombstone's own <c>MergedToUserId</c>, which the cross-section
    /// read resolves away. Filtering the warmed snapshot costs no query.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, UserInfo>> GetRawUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var wanted = userIds.ToHashSet();
        return (await userService.GetAllRawUserInfosAsync(ct))
            .Where(u => wanted.Contains(u.Id))
            .ToDictionary(u => u.Id);
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

        // Verify the pending email only when its owner survives; if the admin flipped direction,
        // the fan-out moves it to the survivor instead.
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

        var survivor = await userService.GetRawUserInfoAsync(survivorUserId, ct)
            ?? throw new InvalidOperationException($"Survivor user {survivorUserId} not found.");
        var archived = await userService.GetRawUserInfoAsync(archivedUserId, ct)
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

        // 1. Move every section's user-keyed rows archived -> survivor. Each commits
        //    independently and re-moving is a no-op, so the step is retryable.
        foreach (var merger in userMerges)
            await merger.ReassignAsync(archivedUserId, survivorUserId, adminUserId, now, ct);

        // 2. Settle the pending email the row-collapse missed. A missing or consumed one is
        //    the desired end state, so the result is ignored.
        if (pendingEmailIdToVerify is Guid pendingId)
            await userRepository.MarkUserEmailVerifiedAsync(pendingId, now, ct);

        // 3. Tombstone the archived account LAST: the observable commit point. No wipe —
        //    chain-follow reads need the redirect.
        await userService.AnonymizeForMergeAsync(archivedUserId, survivorUserId, now, ct);

        // 4. Audit.
        await auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(User), archivedUserId,
            $"Folded archived {archivedUserId} into survivor {survivorUserId}. Notes: {notes ?? "(none)"}",
            adminUserId,
            relatedEntityId: survivorUserId, relatedEntityType: nameof(User));

        // 5. Best-effort: the tombstone already makes leftover requests self-reconcilable.
        await CloseRequestsForPairAsync(survivorUserId, archivedUserId, adminUserId, now, notes, ct);
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
        return new(userId, "(unknown user)", null, null, null, null);
    }

    // Filters GetPendingAsync in memory rather than adding a pair-scoped query; the set is tiny.
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

        // One transaction, or a dangling PendingEmailId blocks a future Accept.
        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
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

        // After commit.
        await userInfoRefresher.RefreshAsync(request.TargetUserId, ct);
    }

    public async Task ReconcileMergedRequestAsync(
        Guid requestId, Guid adminUserId, CancellationToken ct = default)
    {
        var request = await mergeRepository.GetByIdPlainAsync(requestId, ct)
            ?? throw new InvalidOperationException("Merge request not found.");
        if (request.Status != AccountMergeRequestStatus.Pending)
            throw new InvalidOperationException("Merge request is not pending.");

        var source = await userService.GetRawUserInfoAsync(request.SourceUserId, ct);
        var target = await userService.GetRawUserInfoAsync(request.TargetUserId, ct);
        // Close only when the pair merged into each other. A side merged into a third account
        // is a different conflict, and closing would drop its unresolved pending email.
        var mergedIntoEachOther =
            source?.MergedToUserId == request.TargetUserId ||
            target?.MergedToUserId == request.SourceUserId;
        if (!mergedIntoEachOther)
            throw new InvalidOperationException(
                "These two accounts are not merged into each other — resolve this request via Merge or Dismiss instead.");

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
        var info = await userService.GetUserInfoAsync(userId, ct);
        var rows = await mergeRepository.GetForUserGdprAsync(info?.AllUserIds ?? [userId], ct);

        var shaped = rows.Select(r => new
        {
            r.Status,
            Role = r.IsTarget ? "Target" : "Source",
            CreatedAt = r.CreatedAt.ToIso8601(),
            ResolvedAt = r.ResolvedAt.ToIso8601()
        }).ToList();

        return [new UserDataSlice(AccountMergeRequests, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AccountMergeRequests] =
                "Partially retained: the merge row (source id, target id, status, dates) is what " +
                "keeps a tombstoned account's history reachable — deleting it would strand every " +
                "chain-followed record and break the erasure's own provability (GDPR Art. 5(2)). " +
                "The requested email address and any admin notes on it are cleared."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        mergeRepository.ScrubPiiForUserAsync(userId, ct);

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
