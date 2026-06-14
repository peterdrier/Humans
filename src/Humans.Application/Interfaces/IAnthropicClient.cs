using Humans.Application.Models;

namespace Humans.Application.Interfaces;

/// <summary>Thin testable wrapper over the Anthropic SDK. Only the calls the agent needs.</summary>
public interface IAnthropicClient
{
    IAsyncEnumerable<AgentTurnToken> StreamAsync(AnthropicRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the exact input-token count for <paramref name="text"/> against
    /// <paramref name="model"/>'s tokenizer, via Anthropic's <c>count_tokens</c> endpoint.
    /// Token counts are model-specific, so pass the model the text will actually be sent to.
    /// </summary>
    Task<int> CountTokensAsync(string model, string text, CancellationToken cancellationToken = default);
}
