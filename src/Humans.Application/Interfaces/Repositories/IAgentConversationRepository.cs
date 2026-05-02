using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

public interface IAgentConversationRepository
{
    Task<AgentConversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AgentConversation> CreateAsync(Guid userId, string locale, CancellationToken cancellationToken);

    Task AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// GDPR-export variant: includes <see cref="AgentConversation.Messages"/>
    /// ordered by <c>CreatedAt</c> so the export contains full transcripts.
    /// Do not use from list/grid pages — message hydration is wasteful there.
    /// </summary>
    Task<IReadOnlyList<AgentConversation>> ListForUserWithMessagesAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentConversation>> ListAllAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(NodaTime.Instant cutoff, CancellationToken cancellationToken);
}
