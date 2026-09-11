using Humans.Surveys.Contracts;
using Humans.Surveys.Services;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Surveys;

/// <summary>Surveys' contribution to the shared "Messaging" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Messaging", [
            // First-party survey tool (own section); Board happens to be its main
            // user today, but it is not Governance.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin, Weight: 30),
            // Self-service approval gate (Workgroups design §11): any active human may author a
            // Draft survey; this is where Board/Admin approve-and-send or reject a submission.
            new("Survey approvals", "SurveyAdmin", "Queue", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin, Weight: 31,
                PillCount: PillCounts.PendingApprovalQueue)
        ], Weight: 100)
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> PendingApprovalQueue(IServiceProvider sp)
    {
        var surveys = sp.GetRequiredService<ISurveyService>();
        var count = (await surveys.GetPendingApprovalQueueAsync(CancellationToken.None)).Count;
        return count > 0 ? count : null;
    }
}
