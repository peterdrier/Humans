using Humans.Gdpr.Contracts;
using NodaTime;
using Humans.Agent.Contracts;
using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

internal interface IAgentService : IApplicationService, IUserDataContributor, IAgentTranscriptRead
{
    /// <summary>
    /// Deletes conversations older than the configured retention window and records the run for the admin
    /// status panel. Returns how many were deleted.
    /// </summary>
    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<AgentTurnToken> AskAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversationListSnapshot>> GetHistoryAsync(
        Guid userId, int take, CancellationToken cancellationToken);

    /// <summary>Null unless it belongs to <paramref name="userId"/>.</summary>
    Task<AgentConversationTranscriptSnapshot?> GetConversationForUserAsync(
        Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// The user-context tail is built as of now. Null when the conversation is missing or not owned by
    /// <paramref name="userId"/>: callers 404, not 403, so someone else's conversation is not leaked.
    /// </summary>
    Task<AgentMyConversationView?> GetMyConversationAsync(
        Guid userId, Guid conversationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversationListSnapshot>> ListAllConversationsForAdminAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>Regenerated with the current code and state, never stored. Null when the conversation is missing.</summary>
    Task<AgentPromptPreview?> GetPromptPreviewForAdminAsync(
        Guid conversationId, CancellationToken cancellationToken);
}

internal sealed record AgentConversationListSnapshot(
    Guid Id,
    Guid UserId,
    string Locale,
    Instant StartedAt,
    Instant LastMessageAt,
    int MessageCount);
