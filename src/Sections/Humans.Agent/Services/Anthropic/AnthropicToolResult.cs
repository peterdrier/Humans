namespace Humans.Agent.Services.Anthropic;

internal sealed record AnthropicToolResult(
    string ToolCallId,
    string Content,
    bool IsError);
