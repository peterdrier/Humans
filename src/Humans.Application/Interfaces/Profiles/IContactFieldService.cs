using Humans.Application.DTOs;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Service for managing contact fields with visibility controls.
/// </summary>
public interface IContactFieldService
{
    /// <summary>
    /// Gets contact fields visible to the viewer for a given profile.
    /// </summary>
    /// <param name="profileId">The profile to get contact fields for.</param>
    /// <param name="viewerUserId">The user viewing the profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Contact fields filtered by visibility level.</returns>
    Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid profileId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all contact fields for a profile (for editing by the owner).
    /// </summary>
    /// <param name="profileId">The profile to get contact fields for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All contact fields for the profile.</returns>
    Task<IReadOnlyList<ContactFieldEditDto>> GetAllContactFieldsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves contact fields for a profile (upsert/delete).
    /// </summary>
    /// <param name="profileId">The profile to save contact fields for.</param>
    /// <param name="fields">The contact fields to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveContactFieldsAsync(
        Guid profileId,
        IReadOnlyList<ContactFieldEditDto> fields,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the maximum visibility level that a viewer can see for a profile owner.
    /// </summary>
    /// <param name="ownerUserId">The user who owns the profile.</param>
    /// <param name="viewerUserId">The user viewing the profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The maximum visibility level the viewer can see.</returns>
    Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Account-merge fold: bulk-moves <c>ContactField</c> rows from
    /// <paramref name="sourceUserId"/>'s profile to <paramref name="targetUserId"/>'s
    /// profile. Same-<c>(FieldType, Value)</c> rows collapse — source's row is
    /// dropped when target already has a row with the same field type and
    /// (case-insensitive) value; surviving source rows are re-FK'd to the
    /// target's profile. Stamps <c>UpdatedAt</c> on every row touched.
    /// Invalidates the FullProfile cache for both users so admin/search/profile
    /// surfaces reflect the move. Returns the count of <c>ContactField</c> rows
    /// attributed to <paramref name="targetUserId"/>'s profile after dedup.
    /// Called only by <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken cancellationToken = default);
}
