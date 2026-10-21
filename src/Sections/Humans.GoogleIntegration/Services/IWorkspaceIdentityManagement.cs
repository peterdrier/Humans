using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// GoogleIntegration-internal surface over <c>google_workspace_identities</c>: designating the
/// identity and recording sync verdicts.
/// </summary>
internal interface IWorkspaceIdentityManagement : IGoogleIntegrationService, IGoogleIntegrationWrite
{
    /// <summary>
    /// Designates <paramref name="email"/> (one of the member's verified addresses) as their
    /// Workspace identity, audited as <c>UserEmailGoogleSet</c>. Re-asserting the current address
    /// clears a <see cref="GoogleEmailStatus.Rejected"/> verdict so sync retries it. False when the
    /// address is not a verified address of the member, or the member has a verified
    /// <c>@nobodies.team</c> address and this is not one (that address always wins).
    /// </summary>
    Task<bool> SetAsync(Guid userId, string email, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Records Google's verdict on <paramref name="attemptedEmail"/>, the identity address a sync
    /// call targeted. Dropped (false) when the member's identity has since moved to another
    /// address, when nothing changes, or when it would turn <see cref="GoogleEmailStatus.Rejected"/>
    /// back into <see cref="GoogleEmailStatus.Valid"/> (a rejection is terminal for its address).
    /// </summary>
    Task<bool> SetStatusFromSyncAsync(
        Guid userId, string attemptedEmail, GoogleEmailStatus status, CancellationToken ct = default);
}

