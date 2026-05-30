using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Governance;

/// <summary>
/// Service for computing membership status.
/// </summary>
/// <remarks>
/// Cross-section consumers that only read should inject
/// <see cref="IMembershipCalculatorRead"/> instead of this full interface.
/// The members declared directly here (status/role computation used in-section)
/// are not part of the cross-section read surface.
/// </remarks>
public interface IMembershipCalculator : IMembershipCalculatorRead, IApplicationService
{
    /// <summary>
    /// Computes the current membership status for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed membership status.</returns>
    Task<MembershipStatus> ComputeStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has any active roles.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the user has active roles.</returns>
    Task<bool> HasActiveRolesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has any expired (past grace period) consents for a specific team.
    /// Uses per-document GracePeriodDays.
    /// </summary>
    Task<bool> HasAnyExpiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);
}
