using Humans.Surveys.Services;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Surveys;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Surveys", [
            // App access, not Board: authoring is self-service and the index is scoped to what the viewer authored,
            // so a Board gate would leave authors with a page they may use and no way to reach it.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.AppAccess),
            new("Approvals", "SurveyAdmin", "Queue", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin,
                PillCount: PillCounts.PendingApprovalQueueAsync)
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> PendingApprovalQueueAsync(IServiceProvider sp)
    {
        var surveys = sp.GetRequiredService<ISurveyService>();
        var count = (await surveys.GetPendingApprovalQueueAsync(CancellationToken.None)).Count;
        return count > 0 ? count : null;
    }
}
