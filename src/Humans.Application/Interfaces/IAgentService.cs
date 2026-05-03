using System.Collections.Generic;
using System.Threading;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Models;

namespace Humans.Application.Interfaces;

public interface IAgentService : IUserDataContributor
{
    IAsyncEnumerable<AgentTurnToken> AskAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<Humans.Domain.Entities.AgentConversation>> GetHistoryAsync(
        Guid userId, int take, CancellationToken cancellationToken);

    Task DeleteConversationAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Admin-only listing of all conversations across users (for /Agent/Admin/Conversations).</summary>
    Task<IReadOnlyList<Humans.Domain.Entities.AgentConversation>> ListAllConversationsForAdminAsync(
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
