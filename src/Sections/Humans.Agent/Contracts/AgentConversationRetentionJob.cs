using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Agent.Contracts;

/// <summary>
/// Nightly purge of agent conversations past the retention window.
/// </summary>
/// <remarks>
/// Drives <see cref="IAgentConversationRetention"/>: the retention window, the purge and the
/// last-run record all belong to the section, and a Base job holding the section's
/// repository was a layer skip as well as a contracts-leaf three interfaces wide.
///
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-5
/// (nobodies-collective/Humans#866), so shim and body are both Agent's. It sits under
/// <c>Contracts/</c> because Shell names the concrete type at registration and HUM0034 makes
/// every other public type in a section an error.
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
