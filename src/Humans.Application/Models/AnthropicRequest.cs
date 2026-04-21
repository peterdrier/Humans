namespace Humans.Application.Models;

using System.Collections.Generic;

public sealed record AnthropicRequest(
    string Model,
    string SystemCacheablePrefix,
    IReadOnlyList<AnthropicMessage> Messages,
    IReadOnlyList<AnthropicToolDefinition> Tools,
    int MaxOutputTokens);

public sealed record AnthropicMessage(
    string Role,          // "user" | "assistant" | "tool"
    string? Text,
    IReadOnlyList<AnthropicToolCall>? ToolCalls,
    IReadOnlyList<AnthropicToolResult>? ToolResults);

public sealed record AnthropicToolDefinition(
    string Name,
    string Description,
    string JsonSchema);
