using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Profiles;

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
