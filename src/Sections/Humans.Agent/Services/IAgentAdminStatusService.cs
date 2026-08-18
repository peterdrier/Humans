using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

/// <summary>
/// Read-only aggregator for the admin status view (issue #709). Computes
/// usage / spend / refusal / top-doc / top-user windows from the per-message
/// projection emitted by <see cref="Repositories.IAgentRepository"/> and
/// folds in store snapshots (rate-limit remaining capacity, retention job
/// last-run). No mutations.
/// </summary>
internal interface IAgentAdminStatusService : IApplicationService
{
    Task<AgentAdminStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>One aggregated status report, populated by
/// <see cref="IAgentAdminStatusService.GetStatusAsync"/>.</summary>
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
