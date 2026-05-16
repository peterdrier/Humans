using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

public class AgentConversationRetentionJob : IRecurringJob
{
    private readonly IAgentRepository _repo;
    private readonly IAgentSettingsService _settings;
    private readonly IAgentRetentionRunStore _runStore;
    private readonly IClock _clock;
    private readonly ILogger<AgentConversationRetentionJob> _logger;

    public AgentConversationRetentionJob(
        IAgentRepository repo,
        IAgentSettingsService settings,
        IAgentRetentionRunStore runStore,
        IClock clock,
        ILogger<AgentConversationRetentionJob> logger)
    {
        _repo = repo; _settings = settings; _runStore = runStore;
        _clock = clock; _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(_settings.Current.RetentionDays);
        var deleted = await _repo.PurgeConversationsOlderThanAsync(cutoff, cancellationToken);

        // Always record the run — the admin status panel needs the timestamp
        // even when nothing was deleted, so an operator can confirm the job
        // is alive. Recording happens after Purge so a thrown exception
        // surfaces as "last run was earlier" rather than a misleading green tick.
        _runStore.Record(now, deleted);

        if (deleted > 0)
        {
            // Warning so the entry is visible in the prod log viewer (Warning+ default).
            _logger.LogWarning(
                "AgentConversationRetentionJob deleted {Count} conversations older than {Cutoff}",
                deleted, cutoff);
        }
    }
}
