using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Feedback.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Feedback;

/// <summary>Feedback's contribution to the "Issues" admin group, after Issues' own queue (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Issues", [
            new("Feedback queue", "Feedback", "Index", null, null, "fa-solid fa-comment-dots", PolicyNames.AdminOnly, Weight: 10,
                 PillCount: PillCounts.FeedbackQueue)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> FeedbackQueue(IServiceProvider sp)
    {
        var feedback = sp.GetRequiredService<IFeedbackServiceRead>();
        var count = await feedback.GetActionableCountAsync(CancellationToken.None);
        return count > 0 ? count : null;
    }
}
