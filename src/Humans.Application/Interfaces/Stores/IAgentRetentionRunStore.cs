using Humans.Application.Models;
using NodaTime;

namespace Humans.Application.Interfaces.Stores;

/// <summary>
/// Singleton, in-process record of the last <c>AgentConversationRetentionJob</c>
/// run. The job writes; the admin status page reads. State is intentionally
/// not persisted — a process restart resets the snapshot to "never run" and
/// the next scheduled job execution refreshes it.
/// </summary>
public interface IAgentRetentionRunStore
{
    AgentRetentionRunSnapshot Snapshot { get; }
    void Record(Instant runAt, int deletedCount);
}
