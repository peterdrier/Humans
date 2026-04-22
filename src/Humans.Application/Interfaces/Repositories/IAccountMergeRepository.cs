using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>account_merge_requests</c> table. The only non-test
/// file that may write to this DbSet after the Profile-section §15 cleanup
/// (issue #557).
/// </summary>
/// <remarks>
/// Read methods for admin listing eagerly include <see cref="AccountMergeRequest.TargetUser"/>,
/// <see cref="AccountMergeRequest.SourceUser"/>, and <see cref="AccountMergeRequest.ResolvedByUser"/>
/// because the admin view needs display info for all three in a single fetch.
/// These are cross-domain navs from the merge table into <c>AspNetUsers</c>;
/// we tolerate them here because the Users section owns no distinct
/// "display-info" DTO and the merge table sits alongside user identity in the
/// table ownership map (§8, Users/Identity section).
/// </remarks>
public interface IAccountMergeRepository
{
    /// <summary>
    /// Returns all pending merge requests for admin review, ordered by
    /// <c>CreatedAt</c> ascending. Includes the source and target User
    /// navigation properties for display. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequest>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single merge request by id with source, target, and resolver
    /// user navigation properties loaded. Tracked for modification.
    /// </summary>
    Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns a single merge request by id without navigation properties.
    /// Tracked for modification.
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
