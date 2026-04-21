using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

public interface IAgentSettingsService
{
    AgentSettings Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken);
}
