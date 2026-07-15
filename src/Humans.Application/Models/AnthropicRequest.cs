namespace Humans.Application.Models;

using System.Collections.Generic;

public sealed record AnthropicRequest(
    string Model,
    string SystemCacheablePrefix,
    IReadOnlyList<AnthropicMessage> Messages,
    IReadOnlyList<AnthropicToolDefinition> Tools,
    int MaxOutputTokens,
    // Sent as tool_choice "none" so the model must answer in text from the tool
    // results already in Messages. Tools stay defined — the API rejects requests
    // whose history contains tool_use blocks without tool definitions.
    bool DisallowToolUse = false);

public sealed record AnthropicMessage(
    string Role,          // "user" | "assistant" | "tool"
    string? Text,
    IReadOnlyList<AnthropicToolCall>? ToolCalls,
    IReadOnlyList<AnthropicToolResult>? ToolResults);

public sealed record AnthropicToolDefinition(
    string Name,
    string Description,
    string JsonSchema);
