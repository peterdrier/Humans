using Humans.Application.Interfaces;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

public class AgentConversationRetentionJob : IRecurringJob
{
    private readonly HumansDbContext _db;
    private readonly IAgentSettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<AgentConversationRetentionJob> _logger;

    public AgentConversationRetentionJob(
        HumansDbContext db,
        IAgentSettingsService settings,
        IClock clock,
        ILogger<AgentConversationRetentionJob> logger)
    {
        _db = db; _settings = settings; _clock = clock; _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetCurrentInstant() - Duration.FromDays(_settings.Current.RetentionDays);
        var toDelete = await _db.AgentConversations
            .Where(c => c.LastMessageAt < cutoff)
            .ToListAsync(cancellationToken);
        _db.AgentConversations.RemoveRange(toDelete);
        await _db.SaveChangesAsync(cancellationToken);
        var deleted = toDelete.Count;

        _logger.LogInformation(
            "AgentConversationRetentionJob deleted {Count} conversations older than {Cutoff}",
            deleted, cutoff);
    }
}
