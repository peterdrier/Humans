using Humans.Consent.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Onboarding;

/// <summary>Onboarding's admin sidebar group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Onboarding", [
            new("Review", "OnboardingReview", "Index", null, null, "fa-solid fa-clipboard-check", PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueue(IServiceProvider sp)
    {
        // Pending-consent-review count: Users' UserInfo snapshot plus Consent's review map
        // (ConsentReviewPredicates.NeedsConsentReview), read through their public surfaces.
        var users = sp.GetRequiredService<IUserService>();
        var reviews = await sp.GetRequiredService<IConsentService>().GetConsentReviewsAsync();
        var count = (await users.GetAllUserInfosAsync()).Count(u =>
            u.NeedsConsentReview(reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id)));
        return count > 0 ? count : null;
    }
}
