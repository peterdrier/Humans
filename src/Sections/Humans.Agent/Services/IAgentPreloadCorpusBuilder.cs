using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal interface IAgentPreloadCorpusBuilder
{
    Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fetches the community KB and rebuilds every tier's corpus, no restart needed. False when a fetch failed and
    /// the previous corpus was kept.
    /// </summary>
    Task<bool> ReloadAllAsync(CancellationToken cancellationToken = default);
}
