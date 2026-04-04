namespace Humans.Application.Interfaces;

/// <summary>
/// Service for provisioning @nobodies.team email accounts.
/// Encapsulates the 4-step provisioning flow:
///   1. Capture recovery email (before notification target changes)
///   2. Provision Google Workspace account
///   3. Link @nobodies.team email (changes notification target)
///   4. Send credentials email to recovery address
/// </summary>
public interface IEmailProvisioningService
{
    /// <summary>
    /// Provisions a new @nobodies.team email account for a user.
    /// </summary>
    /// <param name="userId">The user to provision the account for.</param>
    /// <param name="emailPrefix">The prefix part of the email (before @nobodies.team).</param>
    /// <param name="provisionedByUserId">The user performing the provisioning (for audit).</param>
    /// <returns>Result indicating success or failure with details.</returns>
    Task<EmailProvisioningResult> ProvisionNobodiesEmailAsync(
        Guid userId,
        string emailPrefix,
        Guid provisionedByUserId);
}

/// <summary>
/// Result of an email provisioning attempt.
/// </summary>
public record EmailProvisioningResult(
    bool Success,
    string? FullEmail = null,
    string? RecoveryEmail = null,
    string? ErrorMessage = null);
