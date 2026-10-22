using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Feedback.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Feedback;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Issues", [
            new("Feedback queue", "Feedback", "Index", null, null, "fa-solid fa-comment-dots", PolicyNames.AdminOnly, Weight: 10,
                 PillCount: PillCounts.FeedbackQueueAsync)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> FeedbackQueueAsync(IServiceProvider sp)
    {
        var feedback = sp.GetRequiredService<FeedbackService>();
        var count = await feedback.GetActionableCountAsync(CancellationToken.None);
        return count > 0 ? count : null;
    }
}
