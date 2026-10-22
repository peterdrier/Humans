using NodaTime;
using Humans.Agent.Models;

namespace Humans.Agent.Services.Stores;

/// <summary>The job writes and the status view reads, so the snapshot is published with Interlocked.Exchange.</summary>
internal sealed class AgentRetentionRunStore : IAgentRetentionRunStore
{
    private AgentRetentionRunSnapshot _snapshot = new(LastRunAt: null, LastDeletedCount: 0);

    public AgentRetentionRunSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Record(Instant runAt, int deletedCount) =>
        Interlocked.Exchange(ref _snapshot, new AgentRetentionRunSnapshot(runAt, deletedCount));
}
