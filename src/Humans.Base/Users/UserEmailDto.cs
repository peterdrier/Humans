
namespace Humans.Users.Contracts;

public record UserEmailDto(
    Guid Id,
    string Email,
    bool IsVerified,
    string? Provider,
    string? ProviderKey,
    bool IsPrimary,
    ContactFieldVisibility? Visibility);

/// <summary>
/// <c>CanUnlink</c>: the row carries a provider login and another verified address would remain, as
/// <see cref="IUserEmailService.UnlinkAsync"/> requires.
/// </summary>
public record UserEmailEditDto(
    Guid Id,
    string Email,
    bool IsVerified,
    string? Provider,
    string? ProviderKey,
    bool IsPrimary,
    ContactFieldVisibility? Visibility,
    bool IsPendingVerification,
    bool IsMergePending = false,
    bool CanUnlink = false);

/// <summary>A refusal has an empty id and token.</summary>
/// <param name="EmailId">The token is bound to it, so the verification URL must carry it back.</param>
/// <param name="Token">For the confirmation URL.</param>
/// <param name="IsConflict">Already verified on another account (merge flow).</param>
/// <param name="ErrorKey">Refusal resource key.</param>
public record AddEmailResult(Guid EmailId, string Token, bool IsConflict, string? ErrorKey = null);

public record UpdateEmailResult(bool Succeeded, string? ErrorKey = null);

public record DeleteEmailResult(bool Succeeded, string? ErrorKey = null);

public record UnlinkEmailResult(bool Succeeded, string? ErrorKey = null);

/// <summary><c>Email</c> is empty for a refusal; <c>MergeRequestCreated</c> means verification opened a merge request instead.</summary>
public record VerifyEmailResult(string Email, bool MergeRequestCreated, string? ErrorKey = null);
