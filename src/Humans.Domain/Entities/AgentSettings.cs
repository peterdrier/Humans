using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton row (<c>Id = 1</c> enforced in EF configuration). Admin-editable at
/// <c>/Admin/Agent/Settings</c>. Mirrored in-memory by <c>IAgentSettingsStore</c>.
/// </summary>
public class AgentSettings
{
    public int Id { get; init; } = 1;

    public bool Enabled { get; set; }
    public string Model { get; set; } = "claude-sonnet-4-6";
    public AgentPreloadConfig PreloadConfig { get; set; } = AgentPreloadConfig.Tier1;

    public int DailyMessageCap { get; set; } = 30;
    public int HourlyMessageCap { get; set; } = 10;
    public int DailyTokenCap { get; set; } = 50000;
    public int RetentionDays { get; set; } = 90;

    public Instant UpdatedAt { get; set; }
}
