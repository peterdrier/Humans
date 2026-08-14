using Humans.Agent.Domain;

namespace Humans.Agent.Services.Stores;

internal interface IAgentSettingsStore
{
    AgentSettings Current { get; }
    void Set(AgentSettings settings);
}
