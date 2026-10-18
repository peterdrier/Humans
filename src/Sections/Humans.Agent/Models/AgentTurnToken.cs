using System.Text.Json.Serialization;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Models;

/// <summary>One chunk of a streamed agent turn. Either a text delta, a tool-call intent,
/// an issue proposal (route_to_issue handoff), or the finalizer. These are mutually exclusive.
/// Provider-only Usage snapshots carry cumulative counters before completion and may appear
/// without another payload; they are never serialized into client SSE frames.</summary>
internal sealed record AgentTurnToken(
    string? TextDelta,
    AnthropicToolCall? ToolCall,
    AgentTurnFinalizer? Finalizer,
    AgentIssueProposal? IssueProposal = null,
    [property: JsonIgnore] AgentTurnFinalizer? Usage = null);
