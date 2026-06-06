using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Service for managing account merge requests.
/// </summary>
public interface IAccountMergeService : IApplicationService
{
    /// <summary>
    /// Gets all pending merge requests for admin review.
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequestSnapshot>> GetPendingRequestsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a merge request by ID with navigation properties loaded.
    /// </summary>
    Task<AccountMergeRequestSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Accepts a merge request, routing through <see cref="MergeAsync"/>. The admin picks
    /// which of the request's two accounts survives via <paramref name="survivorUserId"/>
    /// (it must be the request's source or target), so a wrong-direction request can be
    /// flipped at accept time. The other account is archived/tombstoned.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the request is missing, not pending, or <paramref name="survivorUserId"/>
    /// is not one of the request's two accounts.
    /// </exception>
    Task AcceptAsync(Guid requestId, Guid adminUserId, Guid survivorUserId,
        string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// The one merge primitive. Folds <paramref name="archivedUserId"/> into
    /// <paramref name="survivorUserId"/> via the <c>IUserMerge</c> fan-out, settles the
    /// optional pending email (non-fatal), then tombstones the archived account LAST —
    /// the observable commit point and source of truth. Ordered, with no wrapping
    /// cross-section transaction, so it is safely retryable. Best-effort closes any
    /// pending merge requests for the pair.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if survivor==archived, either user is missing, or either is already tombstoned.
    /// </exception>
    Task MergeAsync(
        Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
        string? notes = null, Guid? pendingEmailIdToVerify = null,
        CancellationToken ct = default);

    /// <summary>
    /// Rejects a merge request: removes the pending email, no changes to accounts.
    /// </summary>
    Task RejectAsync(Guid requestId, Guid adminUserId, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// Closes a pending merge request whose two accounts are already merged — a
    /// pre-existing orphan from before the engine self-reconciled merges. Marks the
    /// request Accepted with NO data or email mutation (the merge already happened).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the request is missing, not pending, or neither account is merged
    /// (resolve those via <see cref="MergeAsync"/> or <see cref="RejectAsync"/> instead).
    /// </exception>
    Task ReconcileMergedRequestAsync(Guid requestId, Guid adminUserId, CancellationToken ct = default);

    // ---- Methods added for Profile-section migration (§15 Step 0) ----
    // Previously, UserEmailService read AccountMergeRequests via direct DbContext.
    // These methods route those reads through the owning service.

    /// <summary>
    /// Returns the subset of email IDs that have a pending merge request.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the target user already has a pending merge request
    /// for the given email (case-insensitive, including gmail/googlemail alternates).
    /// </summary>
    Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if there is a pending merge request for the given pending email ID.
    /// </summary>
    Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new merge request.
    /// </summary>
    Task CreateAsync(AccountMergeRequest request, CancellationToken ct = default);
}

public sealed record AccountMergeRequestSnapshot(
    Guid Id,
    string Email,
    AccountMergeUserSnapshot TargetUser,
    AccountMergeUserSnapshot SourceUser,
    AccountMergeRequestStatus Status,
    Instant CreatedAt,
    Instant? ResolvedAt,
    string? ResolvedByDisplayName,
    string? AdminNotes);

public sealed record AccountMergeUserSnapshot(
    Guid Id,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    string? PreferredLanguage,
    Instant? LastLoginAt);
