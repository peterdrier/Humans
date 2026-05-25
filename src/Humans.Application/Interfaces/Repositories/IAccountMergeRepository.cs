using Humans.Domain.Entities;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>account_merge_requests</c> table. The only non-test
/// file that may write to this DbSet after the Profile-section §15 cleanup
/// (issue #557).
/// </summary>
/// <remarks>
/// Read methods return the merge-request rows with scalar user IDs only.
/// Display info for the source/target/resolver users is stitched in
/// <c>AccountMergeService</c> via <c>IUserService</c> (§6b), never via the
/// cross-domain <c>AspNetUsers</c> navigation properties.
/// </remarks>
[Section("Humans")]
public interface IAccountMergeRepository : IRepository
{
    /// <summary>
    /// Returns all pending merge requests for admin review, ordered by
    /// <c>CreatedAt</c> ascending. Scalar user IDs only — the service stitches
    /// display info via <c>IUserService</c>. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequest>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single merge request by id. Scalar user IDs only — the service
    /// stitches display info via <c>IUserService</c>. Tracked for modification.
    /// </summary>
    Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns a single merge request by id, read-only (AsNoTracking). Use when
    /// the caller does not modify the entity.
    /// </summary>
    Task<AccountMergeRequest?> GetByIdPlainAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns read-only summaries of merge requests where the user is either
    /// the source or the target, ordered by <c>CreatedAt</c> descending.
    /// Used by the GDPR export contributor.
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequestGdprRow>> GetForUserGdprAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of email IDs that have a pending merge request.
    /// Used by <c>UserEmailService.GetUserEmailsAsync</c> to flag
    /// merge-in-progress emails.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a pending merge request exists for the target user and
    /// the given normalized email (or alternate gmail/googlemail form).
    /// </summary>
    Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if a pending merge request exists for the given pending
    /// email id.
    /// </summary>
    Task<bool> HasPendingForEmailIdAsync(Guid pendingEmailId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new merge request.
    /// </summary>
    Task AddAsync(AccountMergeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing merge request. The entity is attached
    /// and marked as Modified.
    /// </summary>
    Task UpdateAsync(AccountMergeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Read-only row shape for the GDPR export contributor. Avoids leaking
/// full <see cref="AccountMergeRequest"/> entities (which carry navigation
/// properties) into the Application-layer service.
/// </summary>
public sealed record AccountMergeRequestGdprRow(
    string Status,
    bool IsTarget,
    NodaTime.Instant CreatedAt,
    NodaTime.Instant? ResolvedAt);
