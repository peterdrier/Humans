using Humans.Domain.Enums;

namespace Humans.Web.Models.Agent;

public sealed class AdminAgentSettingsViewModel
{
    public bool Enabled { get; set; }
    public string Model { get; set; } = "claude-sonnet-4-6";
    public AgentPreloadConfig PreloadConfig { get; set; }
    public int DailyMessageCap { get; set; }
    public int HourlyMessageCap { get; set; }
    public int DailyTokenCap { get; set; }
    public int RetentionDays { get; set; }
}
