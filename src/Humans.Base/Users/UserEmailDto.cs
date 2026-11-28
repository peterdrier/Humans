
namespace Humans.Users.Contracts;

public record UserEmailDto(
    string Email,
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
    bool CanUnlink = false);

/// <summary>A refusal has an empty id and token.</summary>
/// <param name="EmailId">The pending row the token is bound to.</param>
/// <param name="Token">The confirmation link's token.</param>
/// <param name="ErrorKey">Refusal resource key.</param>
public record AddEmailResult(Guid EmailId, string Token, string? ErrorKey = null);

public record UpdateEmailResult(bool Succeeded, string? ErrorKey = null);

public record DeleteEmailResult(bool Succeeded, string? ErrorKey = null);

public record UnlinkEmailResult(bool Succeeded, string? ErrorKey = null);

/// <summary><c>Email</c> is empty for a refusal.</summary>
public record VerifyEmailResult(string Email, string? ErrorKey = null)
{
    /// <summary>The admin target is missing, already verified, or provider-attached.</summary>
    public bool NoPendingVerification { get; init; }
}
