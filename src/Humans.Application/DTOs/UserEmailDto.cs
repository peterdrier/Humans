using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>
/// User email data for display purposes.
/// </summary>
public record UserEmailDto(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsOAuth,
    bool IsNotificationTarget,
    ContactFieldVisibility? Visibility,
    int DisplayOrder);

/// <summary>
/// User email data for the Manage Emails page.
/// </summary>
public record UserEmailEditDto(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsOAuth,
    bool IsNotificationTarget,
    ContactFieldVisibility? Visibility,
    bool IsPendingVerification,
    bool IsMergePending = false);

/// <summary>
/// Result of adding an email address.
/// </summary>
/// <param name="Token">Verification token for building the confirmation URL.</param>
/// <param name="IsConflict">True if the email is already verified on another account (merge flow).</param>
public record AddEmailResult(string Token, bool IsConflict);

/// <summary>
/// Result of verifying an email address.
/// </summary>
/// <param name="Email">The verified email address.</param>
/// <param name="MergeRequestCreated">True if a merge request was created instead of completing verification.</param>
public record VerifyEmailResult(string Email, bool MergeRequestCreated);
