using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Onboarding;

/// <summary>Onboarding's contribution to the shared "Members" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Members", [
            new("Review", "OnboardingReview", "Index", null, null, "fa-solid fa-clipboard-check", PolicyNames.ReviewQueueAccess, Weight: 20,
                 PillCount: PillCounts.ReviewQueue)
        ], Weight: 10)
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueue(IServiceProvider sp)
    {
        // Pending-consent-review count lives on Users' UserInfo snapshot
        // (NeedsConsentReview) — read directly via the section's public read surface.
        var users = sp.GetRequiredService<IUserServiceRead>();
        var count = (await users.GetAllUserInfosAsync()).Count(u => u.NeedsConsentReview);
        return count > 0 ? count : null;
    }
}
