using Humans.Domain.Enums;
using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal interface IAgentPreloadCorpusBuilder
{
    Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a reload+swap of the agent's in-memory knowledge: re-fetches the community KB and
    /// rebuilds the cached preload corpus for every tier. Admin-triggered; no app restart needed.
    /// </summary>
    Task ReloadAllAsync(CancellationToken cancellationToken = default);
}
