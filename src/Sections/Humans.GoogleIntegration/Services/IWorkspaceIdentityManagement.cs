using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

internal interface IWorkspaceIdentityManagement : IGoogleIntegrationService, IGoogleIntegrationWrite
{
    /// <summary>
    /// Audited as <c>UserEmailGoogleSet</c>; re-asserting the current address clears a Rejected verdict. False when the
    /// address isn't one of the member's verified ones, or they have a verified <c>@nobodies.team</c> address (it always wins).
    /// </summary>
    Task<bool> SetAsync(Guid userId, string email, Guid actorUserId, CancellationToken ct = default);

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

    /// <summary>Living members whose legacy <c>user_emails</c> Google flag or verdict has no
    /// <c>google_workspace_identities</c> row yet, with the identity the copy will write.</summary>
    Task<IReadOnlyList<LegacyWorkspaceIdentityRow>> GetUncopiedLegacyIdentitiesAsync(CancellationToken ct = default);

    /// <summary>Copies every pending legacy identity into its own row. Idempotent: a row written
    /// since the list was read is left alone. Returns the rows written.</summary>
    Task<int> CopyLegacyIdentitiesAsync(CancellationToken ct = default);
}

/// <summary>The address Drive grants (<paramref name="DriveEmail"/>) in place of the identity
/// address <paramref name="IdentityEmail"/>.</summary>
internal sealed record DriveEmailOverride(string IdentityEmail, string DriveEmail);

/// <summary>One pending legacy identity: what the read resolves to today, which the copy pins.</summary>
internal sealed record LegacyWorkspaceIdentityRow(Guid UserId, string BurnerName, string Email, GoogleEmailStatus Status);
