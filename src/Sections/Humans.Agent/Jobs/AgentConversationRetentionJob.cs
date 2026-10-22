using Humans.Agent.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Agent.Jobs;

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
