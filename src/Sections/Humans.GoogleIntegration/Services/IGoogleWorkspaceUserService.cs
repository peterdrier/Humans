using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Services;

internal interface IGoogleWorkspaceUserService : IApplicationService
{
    Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Also sets the recovery address and sends the initial password notification. <paramref name="recoveryEmail"/>
    /// should not be @nobodies.team.
    /// </summary>
    [ExternalWrite]
    Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail = null,
        CancellationToken ct = default);

    [ExternalWrite]
    Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default);

    [ExternalWrite]
    Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default);

    [ExternalWrite]
    Task ResetPasswordAsync(string primaryEmail, string newPassword, CancellationToken ct = default);

    Task<WorkspaceUserAccount?> GetAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Google reissues the whole set, invalidating earlier codes. Works for accounts not yet enrolled in 2SV, so a
    /// code and password get a locked-out member through first sign-in.
    /// </summary>
    [ExternalWrite]
    Task<IReadOnlyList<string>> GenerateBackupCodesAsync(
        string primaryEmail,
        CancellationToken ct = default);
}

/// <summary><c>RecoveryEmail</c> is shown to admins so the recovery channel can be checked before a lockout.</summary>
internal sealed record WorkspaceUserAccount(
    string PrimaryEmail,
    string FirstName,
    string LastName,
    bool IsSuspended,
    DateTime CreationTime,
    DateTime? LastLoginTime,
    bool IsEnrolledIn2Sv,
    string? RecoveryEmail = null);
