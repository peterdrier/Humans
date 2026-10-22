using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>The one address Google knows a member by, and Google's verdict on it.</summary>
public interface IGoogleIntegrationService : IApplicationService
{
    /// <summary>
    /// Null without a verified address. Resolved on every read: a verified <c>@nobodies.team</c> address, else the
    /// designated one, else the verified primary, else the first verified. Follows a merge to the survivor.
    /// </summary>
    Task<WorkspaceIdentity?> GetWorkspaceIdentityAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Keyed by the requested id; members without a verified address are absent.</summary>
    Task<IReadOnlyDictionary<Guid, WorkspaceIdentity>> GetWorkspaceIdentitiesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary><paramref name="UserId"/> is the surviving member.</summary>
public sealed record WorkspaceIdentity(Guid UserId, string Email, GoogleEmailStatus Status)
{
    /// <summary>Such an address always wins resolution, so while it stands no other address can become the identity.</summary>
    public bool IsNobodiesTeamAccount => Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase);
}
