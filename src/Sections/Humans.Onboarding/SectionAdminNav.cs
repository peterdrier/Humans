using Humans.Consent.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Onboarding;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Onboarding", [
            new("Review", "OnboardingReview", "Index", null, null, "fa-solid fa-clipboard-check", PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueueAsync)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueueAsync(IServiceProvider sp)
    {
        var users = sp.GetRequiredService<IUserService>();
        var reviews = await sp.GetRequiredService<IConsentService>().GetConsentReviewsAsync();
        var count = (await users.GetAllUserInfosAsync()).Count(u =>
            u.NeedsConsentReview(reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id)));
        return count > 0 ? count : null;
    }
}
