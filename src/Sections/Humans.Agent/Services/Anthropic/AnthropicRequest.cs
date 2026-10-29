namespace Humans.Agent.Services.Anthropic;

using System.Collections.Generic;

internal sealed record AnthropicRequest(
    string Model,
    string SystemCacheablePrefix,
    IReadOnlyList<AnthropicMessage> Messages,
    IReadOnlyList<AnthropicToolDefinition> Tools,
    int MaxOutputTokens,
    // Sent as tool_choice "none" so the model must answer in text from the tool
    // results already in Messages. Tools stay defined — the API rejects requests
    // whose history contains tool_use blocks without tool definitions.
    bool DisallowToolUse = false);

internal sealed record AnthropicMessage(
    string Role,          // "user" | "assistant" | "tool"
    string? Text,
    IReadOnlyList<AnthropicToolCall>? ToolCalls,
    IReadOnlyList<AnthropicToolResult>? ToolResults,
    // When set, sent instead of Text and ToolCalls. Models that think (Claude 5) require an assistant tool-use turn
    // to come back exactly as produced, thinking blocks and their order included.
    IReadOnlyList<AnthropicContentBlock>? AssistantContent = null);

/// <summary>One block of an assistant response, kept so the turn can be replayed unchanged.</summary>
internal abstract record AnthropicContentBlock;

internal sealed record AnthropicTextBlock(string Text) : AnthropicContentBlock;

internal sealed record AnthropicThinkingBlock(string Thinking, string Signature) : AnthropicContentBlock;

internal sealed record AnthropicRedactedThinkingBlock(string Data) : AnthropicContentBlock;

internal sealed record AnthropicToolUseBlock(AnthropicToolCall Call) : AnthropicContentBlock;

internal sealed record AnthropicToolDefinition(
    string Name,
    string Description,
    string JsonSchema);
