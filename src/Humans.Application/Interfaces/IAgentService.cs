using System.Collections.Generic;
using System.Threading;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Models;

namespace Humans.Application.Interfaces;

public interface IAgentService : IApplicationService, IUserDataContributor
{
    IAsyncEnumerable<AgentTurnToken> AskAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<Humans.Domain.Entities.AgentConversation>> GetHistoryAsync(
        Guid userId, int take, CancellationToken cancellationToken);

    /// <summary>Fetches a conversation with messages eagerly loaded only if it
    /// belongs to <paramref name="userId"/>; returns null otherwise. Used by the
    /// user-facing /Agent/Conversation/{id} viewer.</summary>
    Task<Humans.Domain.Entities.AgentConversation?> GetConversationForUserAsync(
        Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// User-facing detail bundle for <c>/Agent/Conversation/{id}</c> (issue
    /// #632). Returns the conversation (messages eagerly loaded) plus the
    /// user-context tail as it would be built right now, so the viewer can
    /// render a "what the agent sees about you currently" panel. Returns
    /// null if the conversation does not exist or is not owned by
    /// <paramref name="userId"/> — callers should 404, not 403, so the
    /// existence of someone else's conversation is not leaked.
    /// </summary>
    Task<AgentMyConversationView?> GetMyConversationAsync(
        Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Admin-only listing of all conversations across users (for /Agent/Admin/Conversations).</summary>
    Task<IReadOnlyList<Humans.Domain.Entities.AgentConversation>> ListAllConversationsForAdminAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>
    /// Admin-only listing of all conversations with messages eagerly loaded so
    /// callers can compute per-conversation aggregates without N+1 round trips.
    /// Used by <c>/api/agent/conversations</c>.
    /// </summary>
    Task<IReadOnlyList<Humans.Domain.Entities.AgentConversation>> ListAllConversationsForAdminWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>Admin-only fetch of a single conversation with messages eagerly loaded.</summary>
    Task<Humans.Domain.Entities.AgentConversation?> GetConversationForAdminAsync(
        Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Admin-only diagnostic: regenerates what would be sent to Anthropic for the
    /// next turn of a conversation, *with the current code and current state*.
    /// Returns null if the conversation does not exist. The preview is never stored.
    /// </summary>
    Task<AgentPromptPreview?> GetPromptPreviewForAdminAsync(
        Guid conversationId, CancellationToken cancellationToken);
}
