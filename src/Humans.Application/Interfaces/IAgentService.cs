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
}
