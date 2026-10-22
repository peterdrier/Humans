using Humans.Base.Interfaces;
using Humans.Base.Attributes;
namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// The recovery email is captured before linking the @nobodies.team address changes the notification target;
/// the credentials go to that recovery address.
/// </summary>
public interface IEmailProvisioningService : IApplicationService
{
    [ExternalWrite]
    Task<EmailProvisioningResult> ProvisionNobodiesEmailAsync(
        Guid userId,
        string emailPrefix,
        Guid provisionedByUserId);
}

/// <summary>
/// A committed account stays successful when the credential handoff fails; <c>WarningMessage</c> says how to recover.
/// </summary>
public record EmailProvisioningResult(
    bool Success,
    string? FullEmail = null,
    string? RecoveryEmail = null,
    string? ErrorMessage = null,
    string? WarningMessage = null);
