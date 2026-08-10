using Humans.Application.Models;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal interface IAgentToolDispatcher
{
    Task<AnthropicToolResult> DispatchAsync(
        AnthropicToolCall call,
        Guid userId,
        CancellationToken cancellationToken);
}
