using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

internal interface IAgentAdminStatusService : IApplicationService
{
    Task<AgentAdminStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}

internal sealed record AgentAdminStatusReport(
    AgentUsageStats Usage24Hours,
    AgentUsageStats Usage7Days,
    AgentUsageStats Usage30Days,
    AgentSpendStats Spend24Hours,
    AgentSpendStats Spend7Days,
    AgentSpendStats Spend30Days,
    AgentSpendStats SpendMtd,
    int Unanswered7DaysCount,
    IReadOnlyList<AgentOutcomeBucket> Outcomes7Days,
    IReadOnlyList<AgentTopDoc> TopDocs7Days,
    IReadOnlyList<AgentTopUser> TopUsers7Days,
    AgentRetentionRunSnapshot Retention,
    AgentBalanceStatus Balance,
    bool SettingsStoreWarm);
