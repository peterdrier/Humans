namespace Humans.Application.Models;

public sealed record AgentTurnFinalizer(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    string Model,
    string? StopReason);
