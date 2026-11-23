using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Users.Services;

internal interface IAccountMergeService : IApplicationService
{
    /// <summary>Requests left over from before merges were self-service. Nothing creates new ones.</summary>
    Task<IReadOnlyList<AccountMergeRequestSnapshot>> GetPendingRequestsAsync(CancellationToken ct = default);

    /// <summary>
    /// The one merge primitive: runs the <c>IUserMerge</c> fan-out, settles the optional pending email (non-fatal),
    /// then tombstones the archived account last, which is the commit point. No wrapping transaction, so it is
    /// safely retryable: a fault part-way is audited as <c>AccountMergeFailed</c> and merging the same pair
    /// again finishes it. Returns a refusal if the ids are equal, either user is missing, or either is already
    /// tombstoned (merged or deleted). <paramref name="actorUserId"/> is the admin, or the account holder
    /// merging their own two accounts.
    /// </summary>
    Task<AccountMergeResult> MergeAsync(
        Guid survivorUserId, Guid archivedUserId, Guid actorUserId,
        string? notes = null, Guid? pendingEmailIdToVerify = null,
        CancellationToken ct = default);

    /// <summary>
    /// Dismisses a leftover request from before merges were self-service: removes its pending email; the
    /// accounts are untouched.
    /// </summary>
    Task<AccountMergeResult> RejectAsync(Guid requestId, Guid adminUserId, string? notes = null, CancellationToken ct = default);
}

internal sealed record AccountMergeRequestSnapshot(
    Guid Id,
    string Email,
    AccountMergeUserSnapshot TargetUser,
    AccountMergeUserSnapshot SourceUser,
    Instant CreatedAt);

internal sealed record AccountMergeUserSnapshot(
    Guid Id);

internal sealed record AccountMergeResult(string? Refusal = null);
