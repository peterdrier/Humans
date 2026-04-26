using Humans.Application.Models;

namespace Humans.Application.Interfaces;

public interface IAgentUserSnapshotProvider
{
    Task<AgentUserSnapshot> LoadAsync(Guid userId, CancellationToken cancellationToken);
}
