namespace Humans.Application.Models;

public sealed record AnthropicToolResult(
    string ToolCallId,
    string Content,
    bool IsError);
