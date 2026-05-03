using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Stores;

public interface IAgentSettingsStore
{
    AgentSettings Current { get; }
    void Set(AgentSettings settings);
}
