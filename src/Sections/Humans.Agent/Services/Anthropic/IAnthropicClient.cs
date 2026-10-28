using Humans.Agent.Models;

namespace Humans.Agent.Services.Anthropic;

internal interface IAnthropicClient
{
    IAsyncEnumerable<AgentTurnToken> StreamAsync(AnthropicRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counted as the cached <c>system</c> block, with a few tokens of request framing (≈, not exact). Counts are
    /// model-specific: pass the model the text will be sent to.
    /// </summary>
    Task<int> CountTokensAsync(string model, string text, CancellationToken cancellationToken = default);

    /// <summary>The models this API key can call, newest first, as Anthropic's Models API reports them.</summary>
    Task<IReadOnlyList<AnthropicModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
}

internal sealed record AnthropicModelInfo(string Id, string DisplayName);
