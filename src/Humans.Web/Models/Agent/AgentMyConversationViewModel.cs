using Humans.Domain.Entities;

namespace Humans.Web.Models.Agent;

/// <summary>
/// Backs the user-facing transcript view at <c>/Agent/Conversation/{id}</c>
/// (issue #632). The conversation is the calling user's own (ownership is
/// enforced server-side; a mismatch returns 404 — see Agent.md invariant 7).
/// <see cref="CurrentUserContextTail"/> is regenerated from the live snapshot
/// for the "what the agent sees about you currently" card; it may differ
/// from what the model saw at the time of any historical turn, so the view
/// surfaces that caveat.
/// </summary>
public sealed record AgentMyConversationViewModel(
    AgentConversation Conversation,
    string CurrentUserContextTail);
