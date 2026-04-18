using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Narrow data-access interface for the <c>account_merge_requests</c> table.
/// Extracted from <see cref="IAccountMergeService"/> so <c>UserEmailService</c>
/// can query merge-request state without pulling in the full account-merge
/// service (which depends on <c>ITeamService</c>) — breaks the
/// <c>TeamService</c> → <c>UserEmailService</c> → <c>AccountMergeService</c>
/// → <c>TeamService</c> DI cycle. See <c>docs/architecture/design-rules.md</c>
/// §15c.
/// </summary>
public interface IAccountMergeRequestRepository
{
    /// <summary>
    /// Returns the subset of <paramref name="emailIds"/> that currently have
    /// a pending <see cref="AccountMergeRequest"/> against them.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default);

    /// <summary>
    /// True if a pending merge request exists for the given
    /// <paramref name="targetUserId"/> + <paramref name="normalizedEmail"/>
    /// (or its optional <paramref name="alternateEmail"/> form, used for the
    /// gmail/googlemail alternate-email rule). Case-insensitive comparison
    /// via <c>EF.Functions.ILike</c>.
    /// </summary>
    Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// True if a pending merge request exists against the given
    /// <paramref name="pendingEmailId"/>.
    /// </summary>
    Task<bool> HasPendingForEmailIdAsync(
        Guid pendingEmailId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new <see cref="AccountMergeRequest"/>. The caller builds
    /// the entity; this method adds it and saves.
    /// </summary>
    Task AddAsync(AccountMergeRequest request, CancellationToken ct = default);
}
