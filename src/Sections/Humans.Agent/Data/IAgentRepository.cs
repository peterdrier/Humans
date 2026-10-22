using NodaTime;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Agent.Data;

internal interface IAgentRepository : IRepository
{

    /// <summary>The singleton row (Id = 1).</summary>
    Task<AgentSettings?> GetSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Throws if the row is missing: the warmup hosted service seeds it.</summary>
    Task<AgentSettings> UpdateSettingsAsync(Action<AgentSettings> mutator, Instant updatedAt, CancellationToken cancellationToken);

    Task<AgentConversation?> GetConversationByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AgentConversation> CreateConversationAsync(Guid userId, string locale, CancellationToken cancellationToken);

    Task AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListConversationsForUserAsync(Guid userId, int take, CancellationToken cancellationToken);

    /// <summary>For GDPR export: includes messages. Do not use from list or grid pages.</summary>
    Task<IReadOnlyList<AgentConversation>> ListConversationsForUserWithMessagesAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListAllConversationsAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>Messages eagerly loaded, so callers compute per-conversation aggregates without N+1.</summary>
    Task<IReadOnlyList<AgentConversation>> ListAllConversationsWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    Task<int> PurgeConversationsOlderThanAsync(Instant cutoff, CancellationToken cancellationToken);

    /// <summary>GDPR Art. 17: messages go with their conversations.</summary>
    Task<int> DeleteConversationsForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Newest first, so callers can stop early on the smaller windows.</summary>
    Task<IReadOnlyList<AgentStatusMessageRow>> ListMessagesSinceAsync(
        Instant since, CancellationToken cancellationToken);

    /// <summary>
    /// Counted from the parent table: a conversation can fall in a window even when none of its messages did.
    /// </summary>
    Task<int> CountConversationsInWindowAsync(
        Instant since, Instant until, CancellationToken cancellationToken);
}
