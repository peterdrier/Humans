using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

public class AgentConversationRetentionJob : IRecurringJob
{
    private readonly IAgentRepository _repo;
    private readonly IAgentSettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<AgentConversationRetentionJob> _logger;

    public AgentConversationRetentionJob(
        IAgentRepository repo,
        IAgentSettingsService settings,
        IClock clock,
        ILogger<AgentConversationRetentionJob> logger)
    {
        _repo = repo; _settings = settings; _clock = clock; _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetCurrentInstant() - Duration.FromDays(_settings.Current.RetentionDays);
        var deleted = await _repo.PurgeConversationsOlderThanAsync(cutoff, cancellationToken);

        if (deleted > 0)
        {
            // Warning so the entry is visible in the prod log viewer (Warning+ default).
            _logger.LogWarning(
                "AgentConversationRetentionJob deleted {Count} conversations older than {Cutoff}",
                deleted, cutoff);
        }
    }
}
