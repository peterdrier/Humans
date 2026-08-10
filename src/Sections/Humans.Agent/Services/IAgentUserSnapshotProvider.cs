using Humans.Application.Models;
using Humans.Agent.Models;

namespace Humans.Agent.Services;

internal interface IAgentUserSnapshotProvider
{
    Task<AgentUserSnapshot> LoadAsync(Guid userId, CancellationToken cancellationToken);
}
