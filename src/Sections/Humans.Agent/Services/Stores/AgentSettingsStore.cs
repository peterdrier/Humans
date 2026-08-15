using Humans.Agent.Services.Stores;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Services.Stores;

internal sealed class AgentSettingsStore : IAgentSettingsStore
{
    // Singleton store; readers (request threads) and the writer (settings save +
    // warmup hosted service) race on the snapshot reference. Interlocked.Exchange
    // gives us atomic publish + visibility without a lock.
    private AgentSettings _current = new()
    {
        Id = 1,
        Enabled = false,
        Model = "claude-sonnet-4-6",
        PreloadConfig = AgentPreloadConfig.Tier2,
        DailyMessageCap = 30,
        HourlyMessageCap = 10,
        DailyTokenCap = 50000,
        RetentionDays = 90,
        UpdatedAt = Instant.MinValue
    };

    public AgentSettings Current => Volatile.Read(ref _current);

    public void Set(AgentSettings settings) => Interlocked.Exchange(ref _current, settings);
}
