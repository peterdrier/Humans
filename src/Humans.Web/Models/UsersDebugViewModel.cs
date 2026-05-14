using Humans.Application;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public sealed record UsersDebugViewModel(
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

public sealed record UserDebugRow(
    Guid UserId,
    bool HasProfile,
    bool HasTicket,
    bool? MarketingOptedOut,
    string DisplayName,
    string BurnerName,
    string LegalName,
    bool? HasConsent)
{
    public static UserDebugRow From(UserInfo info) => new(
        UserId: info.Id,
        HasProfile: info.Profile is not null,
        HasTicket: info.HasTicket,
        MarketingOptedOut: info.MarketingOptedOut,
        DisplayName: info.DisplayName,
        BurnerName: info.Profile?.BurnerName ?? string.Empty,
        LegalName: info.Profile?.FullName ?? string.Empty,
        HasConsent: info.Profile is null
            ? null
            : info.Profile.ConsentCheckStatus == ConsentCheckStatus.Cleared);
}
