using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Reads return scalar user ids; the service stitches display info through
/// <c>IUserManagement</c>, never through <c>AspNetUsers</c> navigations.
/// </summary>
internal interface IAccountMergeRepository : IRepository
{
    /// <summary>Pending requests, oldest first, read-only.</summary>
    Task<IReadOnlyList<AccountMergeRequest>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>Tracked for modification.</summary>
    Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Read-only.</summary>
    Task<AccountMergeRequest?> GetByIdPlainAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: clears the requested email address and any admin notes on
    /// every merge request naming this user. The row itself stays — the merge
    /// chain is what keeps a tombstoned account's history resolvable.
    /// </summary>
    Task<int> ScrubPiiForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns each merge request naming any of the subject's account IDs once.
    /// Marks the subject as target when any of those IDs is the request's target.
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequestGdprRow>> GetForUserGdprAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default);

    /// <summary>Matches the normalized email or its gmail/googlemail alternate.</summary>
    Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default);

    Task AddAsync(AccountMergeRequest request, CancellationToken ct = default);

    Task UpdateAsync(AccountMergeRequest request, CancellationToken ct = default);
}

internal sealed record AccountMergeRequestGdprRow(
    string Status,
    bool IsTarget,
    NodaTime.Instant CreatedAt,
    NodaTime.Instant? ResolvedAt);
