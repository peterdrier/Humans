namespace Humans.Application.Models;

/// <summary>One chunk of a streamed agent turn. Either a text delta, a tool-call intent,
/// an issue proposal (route_to_issue handoff), or the finalizer. Exactly one of the
/// four is non-null.</summary>
public sealed record AgentTurnToken(
    string? TextDelta,
    AnthropicToolCall? ToolCall,
    AgentTurnFinalizer? Finalizer,
    AgentIssueProposal? IssueProposal = null);
