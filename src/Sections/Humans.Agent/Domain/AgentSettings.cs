using NodaTime;

namespace Humans.Agent.Domain;

/// <summary>Singleton row (<c>Id = 1</c>). Mirrored in memory by <c>IAgentSettingsStore</c>.</summary>
internal sealed class AgentSettings
{
    public int Id { get; init; } = 1;

    public bool Enabled { get; set; }
    public string Model { get; set; } = "claude-sonnet-4-6";
    [Obsolete("Unread since the preload became one size. Kept only until its column drop is approved.")]
    public AgentPreloadConfig PreloadConfig { get; set; } = AgentPreloadConfig.Tier1;

    public int DailyMessageCap { get; set; } = 30;
    public int HourlyMessageCap { get; set; } = 10;
    public int DailyTokenCap { get; set; } = 50000;
    public int RetentionDays { get; set; } = 90;

    public Instant UpdatedAt { get; set; }
}
