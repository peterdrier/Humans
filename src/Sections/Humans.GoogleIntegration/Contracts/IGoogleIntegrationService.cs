using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Reads of the Google Workspace identity GoogleIntegration owns (<c>google_workspace_identities</c>):
/// the one address Google knows a member by, and Google's verdict on it.
/// </summary>
public interface IGoogleIntegrationService : IApplicationService
{
    /// <summary>
    /// The member's effective Workspace identity, or null when they have no verified address.
    /// Resolved against the member's verified addresses on every read: a verified
    /// <c>@nobodies.team</c> address, else the designated address, else the verified primary,
    /// else the first verified address. Follows a merge to the surviving account.
    /// </summary>
    Task<WorkspaceIdentity?> GetWorkspaceIdentityAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Bulk <see cref="GetWorkspaceIdentityAsync"/>, keyed by the requested id; members
    /// without a verified address are absent.</summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceIdentity>> GetWorkspaceIdentitiesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary>A member's effective Google Workspace identity.</summary>
/// <param name="UserId">The (surviving) member.</param>
/// <param name="Email">The address Google Workspace knows the member by.</param>
/// <param name="Status">Google's verdict on <paramref name="Email"/>.</param>
public sealed record WorkspaceIdentity(Guid UserId, string Email, GoogleEmailStatus Status)
{
    /// <summary>
    /// The identity is a verified <c>@nobodies.team</c> address. Such an address always wins
    /// resolution, so while it stands no other address of the member can become the identity.
    /// </summary>
    public bool IsNobodiesTeamAccount => Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase);
}
