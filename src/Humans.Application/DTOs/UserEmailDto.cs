using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

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
    bool IsMergePending = false);

/// <summary>
/// Result of adding an email address.
/// </summary>
/// <param name="EmailId">The new <see cref="Domain.Entities.UserEmail"/> row's
/// Id. The verification token is bound to this Id via the token's purpose
/// suffix, so the verification URL must round-trip this Id back to
/// <see cref="Humans.Application.Interfaces.Profiles.IUserEmailService.VerifyEmailAsync"/>
/// to disambiguate when the same user has multiple pending plain rows
/// (issue nobodies-collective/Humans#611).</param>
/// <param name="Token">Verification token for building the confirmation URL.</param>
/// <param name="IsConflict">True if the email is already verified on another account (merge flow).</param>
public record AddEmailResult(Guid EmailId, string Token, bool IsConflict);

/// <summary>
/// Result of verifying an email address.
/// </summary>
/// <param name="Email">The verified email address.</param>
/// <param name="MergeRequestCreated">True if a merge request was created instead of completing verification.</param>
public record VerifyEmailResult(string Email, bool MergeRequestCreated);

/// <summary>
/// A verified UserEmail paired with minimal User info for conflict checks.
/// </summary>
/// <param name="UserId">The owning user's ID.</param>
/// <param name="Email">The verified email address.</param>
/// <param name="ContactSource">Non-null if the user is a pre-provisioned contact.</param>
/// <param name="LastLoginAt">Null for contacts that have never logged in.</param>
public record UserEmailWithUser(
    Guid UserId,
    string Email,
    Humans.Domain.Enums.ContactSource? ContactSource,
    NodaTime.Instant? LastLoginAt);
