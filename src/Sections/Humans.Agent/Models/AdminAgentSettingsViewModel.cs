using Humans.Agent.Domain;
using System.ComponentModel.DataAnnotations;

namespace Humans.Agent.Models;

internal sealed class AdminAgentSettingsViewModel
{
    public bool Enabled { get; set; }
    [Required, StringLength(64)]
    public string Model { get; set; } = "claude-sonnet-4-6";
    public AgentPreloadConfig PreloadConfig { get; set; }
    public int DailyMessageCap { get; set; }
    public int HourlyMessageCap { get; set; }
    public int DailyTokenCap { get; set; }
    public int RetentionDays { get; set; }
}
