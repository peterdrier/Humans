using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Services.Stores;

internal sealed class AgentSettingsStore : IAgentSettingsStore
{
    // Request threads read while the settings save and warmup write: Interlocked.Exchange publishes atomically.
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
