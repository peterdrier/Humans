using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>Accepts both category-aware tokens and legacy campaign-only tokens.</summary>
internal interface IUnsubscribeService : IApplicationService
{
    Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default);

    Task<UnsubscribeTokenResult> ConfirmUnsubscribeAsync(string token, string source, CancellationToken ct = default);
}

internal sealed record UnsubscribeTokenResult
{
    public bool IsValid { get; init; }
    public bool IsExpired { get; init; }
    public bool IsLegacy { get; init; }
    public Guid? UserId { get; init; }
    public MessageCategory? Category { get; init; }

    public static UnsubscribeTokenResult Invalid() => new() { IsValid = false };
    public static UnsubscribeTokenResult Expired() => new() { IsValid = false, IsExpired = true };

    public static UnsubscribeTokenResult Valid(
        Guid userId, MessageCategory category, bool isLegacy = false) =>
        new()
        {
            IsValid = true,
            UserId = userId,
            Category = category,
            IsLegacy = isLegacy
        };
}
