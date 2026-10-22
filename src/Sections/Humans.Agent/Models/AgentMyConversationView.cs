using Humans.Agent.Contracts;

namespace Humans.Agent.Models;

/// <summary>
/// The user-context tail is regenerated now, not snapshotted per turn, so the viewer shows it with a caveat.
/// </summary>
internal sealed record AgentMyConversationView(
    AgentConversationTranscriptSnapshot Conversation,
    string CurrentUserContextTail);
