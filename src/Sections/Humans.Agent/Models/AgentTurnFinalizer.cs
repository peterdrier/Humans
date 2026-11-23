namespace Humans.Agent.Models;

internal sealed record AgentTurnFinalizer(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    string? StopReason,
    Guid ConversationId = default);
