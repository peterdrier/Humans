using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Marketing" — humans who have explicitly opted in to the Marketing
/// communication category (<see cref="UserInfo.MarketingOptedOut"/> == false).
/// Users with no Marketing preference row (default-off) are excluded.
/// </summary>
public sealed class MarketingAudience(IUserService users) : IMailerAudience
{
    public string Key => "marketing";
    public string DisplayName => "Marketing opt-ins";
    public string MailerLiteGroupName => "Humans - Marketing";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await users.GetAllUserInfosAsync(ct);
        return allUsers
            .Where(u => u.MarketingOptedOut == false)
            .Select(u => u.Id)
            .ToHashSet();
    }
}
