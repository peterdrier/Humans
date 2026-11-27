using Humans.Google.Contracts;

namespace Humans.Google.Services;

internal interface IWorkspaceIdentityManagement : IGoogleService
{
    /// <summary>
    /// Audited as <c>UserEmailGoogleSet</c>; re-asserting the current address clears a Rejected verdict.
    /// Returns why it was refused, or null when it was set.
    /// </summary>
    Task<WorkspaceIdentityRefusal?> SetAsync(Guid userId, string email, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// False (dropped) when the identity has moved to another address, nothing changes, or it would turn
    /// <see cref="GoogleEmailStatus.Rejected"/> back into Valid: a rejection is terminal for its address.
    /// </summary>
    Task<bool> SetStatusFromSyncAsync(
        Guid userId, string attemptedEmail, GoogleEmailStatus status, CancellationToken ct = default);

    /// <summary>False (dropped) when the identity has moved to another address or nothing changes.</summary>
    Task<bool> SetDriveEmailFromSyncAsync(
        Guid userId, string attemptedEmail, string driveEmail, CancellationToken ct = default);

    /// <summary>Every recorded Drive address, keyed by member, with the identity address it stands
    /// in for. Callers apply one only while that identity address is still the member's.</summary>
    Task<IReadOnlyDictionary<Guid, DriveEmailOverride>> GetDriveEmailsAsync(CancellationToken ct = default);
}

/// <summary>The address Drive grants (<paramref name="DriveEmail"/>) in place of the identity
/// address <paramref name="IdentityEmail"/>.</summary>
internal sealed record DriveEmailOverride(string IdentityEmail, string DriveEmail);

internal enum WorkspaceIdentityRefusal
{
    /// <summary>The id is not a live account (for example one merged away).</summary>
    AccountNotFound,
    NotTheirEmail,
    Unverified,
    /// <summary>They have a verified <c>@nobodies.team</c> address, which always wins.</summary>
    NobodiesAddressWins
}
