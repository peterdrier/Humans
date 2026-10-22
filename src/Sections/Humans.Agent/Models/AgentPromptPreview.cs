namespace Humans.Agent.Models;

/// <summary>
/// Regenerated on demand, never stored: it shows the current corpus, prompt and user snapshot, not what any past turn
/// sent.
/// </summary>
internal sealed record AgentPromptPreview(
    string Model,
    string SystemPrompt,
    string UserContextTail,
    IReadOnlyList<AgentPromptToolDefinition> Tools,
    IReadOnlyList<AgentPromptHistoryTurn> ReplayedHistory,
    int? SystemPromptTokens);

internal sealed record AgentPromptToolDefinition(string Name, string Description, string JsonSchema);

internal sealed record AgentPromptHistoryTurn(string Role, string Text);
