namespace Humans.Application.Models;

/// <summary>
/// Admin-only diagnostic snapshot of what would be sent to Anthropic for a
/// given conversation if a new turn started right now. Regenerated on demand
/// — never stored — so the preview reflects the *current* preload corpus,
/// system prompt, and user snapshot, not what was sent at the time of any
/// historical turn.
/// </summary>
public sealed record AgentPromptPreview(
    string Model,
    string SystemPrompt,
    string UserContextTail,
    IReadOnlyList<AgentPromptToolDefinition> Tools,
    IReadOnlyList<AgentPromptHistoryTurn> ReplayedHistory);

public sealed record AgentPromptToolDefinition(string Name, string Description, string JsonSchema);

public sealed record AgentPromptHistoryTurn(string Role, string Text);
