using Humans.Agent.Contracts;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Nightly purge of agent conversations past the retention window.
/// </summary>
/// <remarks>
/// Stays in Base because recurring jobs are named by concrete type in Shell's
/// <c>UseHumansRecurringJobs</c> roll-call and there is no <c>ISection</c>-style discovery
/// seam for them yet (design §15.6b). It reaches Agent through
/// <see cref="IAgentConversationRetention"/>: the retention window, the purge and the
/// last-run record all belong to the section, and a Base job holding the section's
/// repository was a layer skip as well as a contracts-leaf three interfaces wide.
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
