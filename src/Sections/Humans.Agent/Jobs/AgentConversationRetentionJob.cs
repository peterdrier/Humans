using Humans.Agent.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Agent.Jobs;

/// <summary>
/// Nightly purge of agent conversations past the retention window.
/// </summary>
/// <remarks>
/// Drives <see cref="IAgentConversationRetention"/>: the retention window, the purge and the
/// last-run record all belong to the section. It sits under <c>Jobs/</c> because Shell names
/// the concrete type at registration and HUM0034 makes every other public type in a section
/// an error.
/// </remarks>
public class AgentConversationRetentionJob(
    IAgentConversationRetention retention,
    ILogger<AgentConversationRetentionJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await retention.PurgeExpiredConversationsAsync(cancellationToken);

        if (deleted > 0)
        {
            // Warning so the entry is visible in the prod log viewer (Warning+ default).
            logger.LogWarning("AgentConversationRetentionJob deleted {Count} conversations", deleted);
        }
    }
}
