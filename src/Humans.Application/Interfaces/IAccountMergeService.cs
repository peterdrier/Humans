using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing account merge requests.
/// </summary>
public interface IAccountMergeService
{
    /// <summary>
    /// Gets all pending merge requests for admin review.
    /// </summary>
    Task<IReadOnlyList<AccountMergeRequest>> GetPendingRequestsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a merge request by ID with navigation properties loaded.
    /// </summary>
    Task<AccountMergeRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Accepts a merge request: migrates data from source to target, archives source account.
    /// </summary>
    Task AcceptAsync(Guid requestId, Guid adminUserId, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// Rejects a merge request: removes the pending email, no changes to accounts.
    /// </summary>
    Task RejectAsync(Guid requestId, Guid adminUserId, string? notes = null, CancellationToken ct = default);
}
