namespace Humans.Application.Models;

public sealed record AnthropicToolCall(
    string Id,
    string Name,
    string JsonArguments);
