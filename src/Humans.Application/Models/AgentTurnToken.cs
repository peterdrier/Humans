namespace Humans.Application.Models;

/// <summary>One chunk of a streamed agent turn. Either a text delta, a tool-call intent,
/// or the finalizer. Exactly one of the three is non-null.</summary>
public sealed record AgentTurnToken(
    string? TextDelta,
    AnthropicToolCall? ToolCall,
    AgentTurnFinalizer? Finalizer);
