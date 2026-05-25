using Humans.Application.Models;
using Humans.Domain.Entities;
using NodaTime;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Single repository for the Agent section. Covers settings, conversations,
/// and messages. The Agent section never injects <c>HumansDbContext</c> directly
/// and its EF model has no cross-section FK or nav wiring — owned-table joins
/// only.
/// </summary>
[Section("Agent")]
public interface IAgentRepository : IRepository
{
    // ---- Settings (singleton row, Id = 1) ------------------------------------

    Task<AgentSettings?> GetSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the singleton row, applies the mutator, stamps <c>UpdatedAt</c>,
    /// saves, and returns the updated row. Throws if the row is missing —
    /// the warmup hosted service guarantees a seeded row.
    /// </summary>
    Task<AgentSettings> UpdateSettingsAsync(Action<AgentSettings> mutator, Instant updatedAt, CancellationToken cancellationToken);

    // ---- Conversations + messages -------------------------------------------

    Task<AgentConversation?> GetConversationByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AgentConversation> CreateConversationAsync(Guid userId, string locale, CancellationToken cancellationToken);

    Task AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListConversationsForUserAsync(Guid userId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// GDPR-export variant: includes <see cref="AgentConversation.Messages"/>
    /// ordered by <c>CreatedAt</c>. Do not use from list/grid pages.
    /// </summary>
    Task<IReadOnlyList<AgentConversation>> ListConversationsForUserWithMessagesAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListAllConversationsAsync(
        bool refusalsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>
    /// Admin-listing variant of <see cref="ListAllConversationsAsync"/> that
    /// eagerly loads each conversation's messages so callers can compute
    /// per-conversation aggregates (refusal/handoff counts, last-message
    /// preview) without N+1 round trips. Used by <c>/api/agent/conversations</c>.
    /// </summary>
    Task<IReadOnlyList<AgentConversation>> ListAllConversationsWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    Task<int> PurgeConversationsOlderThanAsync(Instant cutoff, CancellationToken cancellationToken);

    // ---- Admin status (read-only, in-memory aggregated by callers) ----------

    /// <summary>
    /// Flat per-message projection over <c>agent_messages</c> created at or
    /// after <paramref name="since"/>. The admin status view aggregates these
    /// rows in memory across multiple windows (24h / 7d / 30d). Returned
    /// ordered by <c>CreatedAt</c> descending so callers can early-stop when
    /// computing the smaller windows.
    /// </summary>
    Task<IReadOnlyList<AgentStatusMessageRow>> ListMessagesSinceAsync(
        Instant since, CancellationToken cancellationToken);

    /// <summary>
    /// Count of conversations whose <c>LastMessageAt</c> falls in the given
    /// window. Used by the usage panel — separate from
    /// <see cref="ListMessagesSinceAsync"/> because a conversation row can
    /// fall in a window even if no new messages did (rare, but the
    /// conversation count must come from the parent table).
    /// </summary>
    Task<int> CountConversationsInWindowAsync(
        Instant since, Instant until, CancellationToken cancellationToken);
}
