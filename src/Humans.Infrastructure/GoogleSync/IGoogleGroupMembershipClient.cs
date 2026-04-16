using Google.Apis.CloudIdentity.v1.Data;

namespace Humans.Infrastructure.GoogleSync;

/// <summary>
/// Narrow seam over <see cref="global::Google.Apis.CloudIdentity.v1.CloudIdentityService"/> used by
/// <see cref="Services.GoogleWorkspaceSyncService"/>'s reconciliation + gateway paths.
/// Exists so the reconciliation tests can substitute an in-memory fake without touching the
/// SDK's sealed concrete types. Only the operations the reconciliation path uses are exposed.
/// </summary>
internal interface IGoogleGroupMembershipClient
{
    /// <summary>
    /// Returns all memberships on the given Google Group id. Pagination is handled internally.
    /// </summary>
    /// <exception cref="global::Google.GoogleApiException">
    /// Thrown with <c>Error.Code = 404</c> when the group does not exist in Google and with
    /// <c>Error.Code = 403</c> when the service account lacks access.
    /// </exception>
    Task<IReadOnlyList<Membership>> ListMembershipsAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a MEMBER membership for the given email on the given group id.
    /// </summary>
    /// <exception cref="global::Google.GoogleApiException">
    /// Thrown with <c>Error.Code = 409</c> if the user is already a member, and <c>403</c> for
    /// permission errors or rejected emails.
    /// </exception>
    Task CreateMembershipAsync(string groupId, string userEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the membership identified by its full resource name
    /// (<c>groups/{groupId}/memberships/{membershipId}</c>).
    /// </summary>
    Task DeleteMembershipAsync(string membershipName, CancellationToken cancellationToken);
}
