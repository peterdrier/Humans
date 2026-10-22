using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>Internal: the only caller is <c>UserEmailService</c>, and <c>CreateAsync</c> takes the internal entity.</summary>
internal interface IAccountMergeService : IApplicationService
{
    Task<IReadOnlyList<AccountMergeRequestSnapshot>> GetPendingRequestsAsync(CancellationToken ct = default);

    Task<AccountMergeRequestSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The admin picks the survivor (the request's source or target), so a wrong-direction request can be flipped
    /// here. Throws <see cref="InvalidOperationException"/> if the request is missing or not pending, or
    /// <paramref name="survivorUserId"/> isn't one of its accounts.
    /// </summary>
    Task AcceptAsync(Guid requestId, Guid adminUserId, Guid survivorUserId,
        string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// The one merge primitive: runs the <c>IUserMerge</c> fan-out, settles the optional pending email (non-fatal),
    /// then tombstones the archived account last, which is the commit point. No wrapping transaction, so it is
    /// safely retryable. Throws <see cref="InvalidOperationException"/> if the ids are equal, either user is missing,
    /// or either is already tombstoned.
    /// </summary>
    Task MergeAsync(
        Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
        string? notes = null, Guid? pendingEmailIdToVerify = null,
        CancellationToken ct = default);

    /// <summary>Removes the pending email; the accounts are untouched.</summary>
    Task RejectAsync(Guid requestId, Guid adminUserId, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// Closes an orphan request whose accounts are already merged: marks it Accepted with no data or email change.
    /// Throws <see cref="InvalidOperationException"/> if the request is missing, not pending, or neither account is
    /// merged.
    /// </summary>
    Task ReconcileMergedRequestAsync(Guid requestId, Guid adminUserId, CancellationToken ct = default);

    /// <summary>The subset of <paramref name="emailIds"/> with a pending merge request.</summary>
    Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default);

    /// <summary>Case-insensitive, including the gmail/googlemail twin.</summary>
    Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default);

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
