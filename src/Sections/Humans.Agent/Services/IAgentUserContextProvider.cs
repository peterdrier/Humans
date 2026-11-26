using Humans.Agent.Models;

namespace Humans.Agent.Services;

internal interface IAgentUserContextProvider
{
    Task<AgentUserContext> LoadAsync(Guid userId, CancellationToken cancellationToken);
}
