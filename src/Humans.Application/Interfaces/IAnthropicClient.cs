using System.Collections.Generic;
using System.Threading;
using Humans.Application.Models;

namespace Humans.Application.Interfaces;

/// <summary>Thin testable wrapper over the Anthropic SDK. Only the calls the agent needs.</summary>
public interface IAnthropicClient
{
    IAsyncEnumerable<AgentTurnToken> StreamAsync(AnthropicRequest request, CancellationToken cancellationToken = default);
}
