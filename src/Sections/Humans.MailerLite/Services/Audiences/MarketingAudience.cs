using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>Explicit opt-ins only: no Marketing row (default off) is excluded.</summary>
internal sealed class MarketingAudience(IUserService users, ICommunicationPreferenceService prefs)
    : MailerLiteAudienceBase(users, prefs)
{
    public override string Key => "marketing";
    public override string DisplayName => "Marketing opt-ins";
    public override string MailerLiteGroupName => "Humans - Marketing";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        var marketing = await MarketingPreferencesAsync(ct);
        return allUsers
            .Where(u => marketing.GetValueOrDefault(u.Id) is { OptedOut: false })
            .Select(u => u.Id)
            .ToHashSet();
    }
}
