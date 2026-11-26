namespace Humans.Agent.Services;

internal interface IAgentPreloadCorpusBuilder
{
    Task<string> BuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fetches the Community FAQ and rebuilds the corpus, no restart needed. False when a fetch failed and
    /// the previous corpus was kept.
    /// </summary>
    Task<bool> ReloadAllAsync(CancellationToken cancellationToken = default);
}
