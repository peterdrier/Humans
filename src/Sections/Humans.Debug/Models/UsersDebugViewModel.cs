using Humans.Consent.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Debug.Models;

internal sealed record UsersDebugViewModel(
    IReadOnlyList<UserDebugRow> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    string Sort,
    string Dir)
{
    public int TotalPages => PageSize > 0
        ? (int)Math.Ceiling((double)TotalCount / PageSize)
        : 0;

    public bool IsAsc => string.Equals(Dir, "asc", StringComparison.OrdinalIgnoreCase);
}

internal sealed record UserDebugRow(
    Guid UserId,
    bool HasProfile,
    bool HasTicket,
    bool? MarketingOptedOut,
    string DisplayName,
    string BurnerName,
    string LegalName,
    bool? HasName,
    bool? HasConsent,
    Instant CreatedAt,
    Instant? LastLoginAt)
{
    public static UserDebugRow From(UserInfo info, ConsentReviewInfo? review) => new(
        UserId: info.Id,
        HasProfile: info.Profile is not null,
        HasTicket: info.HasTicket,
        MarketingOptedOut: info.MarketingOptedOut,
        DisplayName: info.BurnerName,
        BurnerName: info.BurnerName,
        LegalName: info.Profile?.FullName ?? string.Empty,
        HasName: info.Profile is null ? null : info.HasRequiredNameFields,
        HasConsent: info.Profile is null
            ? null
            : review?.Status == ConsentCheckStatus.Cleared,
        CreatedAt: info.CreatedAt,
        LastLoginAt: info.LastLoginAt);
}
