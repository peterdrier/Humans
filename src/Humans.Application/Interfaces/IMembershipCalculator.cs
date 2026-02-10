using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for computing membership status.
/// </summary>
public interface IMembershipCalculator
{
    /// <summary>
    /// Computes the current membership status for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed membership status.</returns>
    Task<MembershipStatus> ComputeStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has all required consents.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all required consents are present.</returns>
    Task<bool> HasAllRequiredConsentsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the document versions that a user is missing consent for.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of document version IDs missing consent.</returns>
    Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has any active roles.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the user has active roles.</returns>
    Task<bool> HasActiveRolesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets users whose membership status should be set to Inactive due to missing consent.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user IDs that should be marked inactive.</returns>
    Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters a set of user IDs to only those who have all required consents.
    /// This is a batch operation that avoids N+1 queries.
    /// </summary>
    /// <param name="userIds">The user IDs to filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User IDs that have all required consents.</returns>
    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);
}
