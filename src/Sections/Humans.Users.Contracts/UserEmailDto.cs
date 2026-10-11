
namespace Humans.Users.Contracts;

/// <summary>
/// User email data for display purposes.
/// </summary>
public record UserEmailDto(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsGoogle,
    string? Provider,
    string? ProviderKey,
    bool IsPrimary,
    ContactFieldVisibility? Visibility);

/// <summary>
/// User email data for the Manage Emails page.
/// </summary>
public record UserEmailEditDto(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsGoogle,
    string? Provider,
    string? ProviderKey,
    bool IsPrimary,
    ContactFieldVisibility? Visibility,
    bool IsPendingVerification,
    GoogleEmailStatus GoogleEmailStatus,
    bool IsMergePending = false);

/// <summary>
/// Result of adding an email address.
/// </summary>
/// <param name="EmailId">The new <see cref="UserEmail"/> row's
/// Id. The verification token is bound to this Id via the token's purpose
/// suffix, so the verification URL must round-trip this Id back to
/// <see cref="Humans.Users.Contracts.IUserEmailService.VerifyEmailAsync"/>
/// to disambiguate when the same user has multiple pending plain rows
/// (issue nobodies-collective/Humans#611).</param>
/// <param name="Token">Verification token for building the confirmation URL.</param>
/// <param name="IsConflict">True if the email is already verified on another account (merge flow).</param>
/// <param name="ErrorKey">Expected refusal resource key; refused additions have an empty Id and token.</param>
public record AddEmailResult(Guid EmailId, string Token, bool IsConflict, string? ErrorKey = null);

/// <summary>Result of deleting an email row, including an expected refusal.</summary>
/// <param name="Succeeded">Whether the email row was removed.</param>
/// <param name="ErrorKey">Member-facing refusal resource key, or null.</param>
public record DeleteEmailResult(bool Succeeded, string? ErrorKey = null);

/// <summary>Result of unlinking a sign-in provider, including an expected refusal.</summary>
/// <param name="Succeeded">Whether the provider and email row were removed.</param>
/// <param name="ErrorKey">Member-facing refusal resource key, or null.</param>
public record UnlinkEmailResult(bool Succeeded, string? ErrorKey = null);

/// <summary>
/// Result of verifying an email address.
/// </summary>
/// <param name="Email">The verified email address, or empty for a refusal.</param>
/// <param name="MergeRequestCreated">True if a merge request was created instead of completing verification.</param>
/// <param name="ErrorKey">Localized refusal resource key, or null on successful verification.</param>
public record VerifyEmailResult(string Email, bool MergeRequestCreated, string? ErrorKey = null);
