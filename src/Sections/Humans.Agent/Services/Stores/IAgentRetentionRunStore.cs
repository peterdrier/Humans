using NodaTime;
using Humans.Agent.Models;

namespace Humans.Agent.Services.Stores;

/// <summary>Not persisted: a restart resets it to "never run" until the next job run.</summary>
internal interface IAgentRetentionRunStore
{
    AgentRetentionRunSnapshot Snapshot { get; }
    void Record(Instant runAt, int deletedCount);
}
