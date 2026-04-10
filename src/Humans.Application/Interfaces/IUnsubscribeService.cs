using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Handles token validation, user lookup, and unsubscribe actions
/// for both new category-aware tokens and legacy campaign-only tokens.
/// </summary>
public interface IUnsubscribeService
{
    /// <summary>
    /// Validates an unsubscribe token (new or legacy) and returns the resolved result.
    /// </summary>
    Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Performs the unsubscribe action for a validated token.
    /// </summary>
    Task<UnsubscribeTokenResult> ConfirmUnsubscribeAsync(string token, string source, CancellationToken ct = default);
}

/// <summary>
/// Result of validating an unsubscribe token.
/// </summary>
public record UnsubscribeTokenResult
{
    public bool IsValid { get; init; }
    public bool IsExpired { get; init; }
    public bool IsLegacy { get; init; }
    public Guid? UserId { get; init; }
    public string? DisplayName { get; init; }
    public MessageCategory? Category { get; init; }

    public static UnsubscribeTokenResult Invalid() => new() { IsValid = false };
    public static UnsubscribeTokenResult Expired() => new() { IsValid = false, IsExpired = true };

    public static UnsubscribeTokenResult Valid(
        Guid userId, string displayName, MessageCategory category, bool isLegacy = false) =>
        new()
        {
            IsValid = true,
            UserId = userId,
            DisplayName = displayName,
            Category = category,
            IsLegacy = isLegacy
        };
}
