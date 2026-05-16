using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using NodaTime;

namespace Humans.Infrastructure.Stores;

/// <summary>
/// In-memory snapshot of the last <c>AgentConversationRetentionJob</c> run.
/// Singleton; readers (admin status view) and writer (retention job) race on
/// the snapshot reference, so we publish via <see cref="Interlocked.Exchange"/>
/// to match the pattern used by <c>AgentSettingsStore</c>.
/// </summary>
public sealed class AgentRetentionRunStore : IAgentRetentionRunStore
{
    private AgentRetentionRunSnapshot _snapshot = new(LastRunAt: null, LastDeletedCount: 0);

    public AgentRetentionRunSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Record(Instant runAt, int deletedCount) =>
        Interlocked.Exchange(ref _snapshot, new AgentRetentionRunSnapshot(runAt, deletedCount));
}
