using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Marketing" — humans who have explicitly opted in to the Marketing
/// communication category (<see cref="UserInfo.MarketingOptedOut"/> == false).
/// Users with no Marketing preference row (default-off) are excluded.
/// </summary>
public sealed class MarketingAudience(IUserServiceRead users) : MailerAudienceBase(users)
{
    public override string Key => "marketing";
    public override string DisplayName => "Marketing opt-ins";
    public override string MailerLiteGroupName => "Humans - Marketing";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        return allUsers
            .Where(u => u.MarketingOptedOut == false)
            .Select(u => u.Id)
            .ToHashSet();
    }
}
