using Humans.Domain.Entities;

namespace Humans.Application.Models;

/// <summary>
/// User-facing transcript of the calling user's own agent conversation
/// (issue #632). Bundles the persisted <see cref="AgentConversation"/> with the
/// user-context tail as it would be built *right now*, so the viewer at
/// <c>/Agent/Conversation/{id}</c> can render a "this is what the agent sees
/// about you currently" panel alongside the historical messages. The tail is
/// not snapshotted per turn today — see Agent.md "Open question" — so this
/// record carries the live regenerated value with a UI caveat.
/// </summary>
public sealed record AgentMyConversationView(
    AgentConversation Conversation,
    string CurrentUserContextTail);
