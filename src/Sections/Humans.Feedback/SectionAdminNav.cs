using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Feedback.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Feedback;

/// <summary>Feedback's contribution to the shared "Feedback" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Feedback", [
            new("Feedback queue", "Feedback", "Index", null, null, "fa-solid fa-comment-dots", PolicyNames.AdminOnly, Weight: 0,
                 PillCount: PillCounts.FeedbackQueue)
        ], Weight: 90)
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
