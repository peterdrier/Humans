namespace Humans.Agent.Services.Anthropic;

internal sealed record AnthropicToolCall(
    string Id,
    string Name,
    string JsonArguments);
