using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Infrastructure.Stores;

public sealed class AgentSettingsStore : IAgentSettingsStore
{
    // Safe default mirrors the DB seed so the store is always queryable before the warmup runs.
    private AgentSettings _current = new()
    {
        Id = 1,
        Enabled = false,
        Model = "claude-sonnet-4-6",
        PreloadConfig = AgentPreloadConfig.Tier1,
        DailyMessageCap = 30,
        HourlyMessageCap = 10,
        DailyTokenCap = 50000,
        RetentionDays = 90,
        UpdatedAt = Instant.MinValue
    };

    public AgentSettings Current => _current;

    public void Set(AgentSettings settings) => _current = settings;
}
