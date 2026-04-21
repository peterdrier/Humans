using Humans.Application.Models;

namespace Humans.Application.Interfaces;

public interface IAgentToolDispatcher
{
    Task<AnthropicToolResult> DispatchAsync(
        AnthropicToolCall call,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken);
}
