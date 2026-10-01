using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal interface IAgentPreloadCorpusBuilder
{
    Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a reload+swap of the agent's in-memory knowledge: re-fetches the community KB and
    /// rebuilds the cached preload corpus for every tier. Admin-triggered; no app restart needed.
    /// Returns false when a fetch failed and the previous preload corpus was retained.
    /// </summary>
    Task<bool> ReloadAllAsync(CancellationToken cancellationToken = default);
}
