using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

internal interface IAgentAdminStatusService : IApplicationService
{
    Task<AgentAdminStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}

internal sealed record AgentAdminStatusReport(
    AgentUsageStats Usage24h,
    AgentUsageStats Usage7d,
    AgentUsageStats Usage30d,
    AgentSpendStats Spend24h,
    AgentSpendStats Spend7d,
    AgentSpendStats Spend30d,
    AgentSpendStats SpendMtd,
    int Refusals7dCount,
    IReadOnlyList<AgentRefusalBucket> Refusals7d,
    IReadOnlyList<AgentTopDoc> TopDocs7d,
    IReadOnlyList<AgentTopUser> TopUsers7d,
    AgentRetentionRunSnapshot Retention,
    AgentBalanceStatus Balance,
    bool SettingsStoreWarm);
