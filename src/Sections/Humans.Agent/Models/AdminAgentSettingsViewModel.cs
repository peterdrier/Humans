using Humans.Agent.Domain;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Agent.Models;

internal sealed class AdminAgentSettingsViewModel
{
    [BindRequired]
    public bool Enabled { get; set; }
    [Required, StringLength(64)]
    [BindRequired]
    public string Model { get; set; } = "claude-sonnet-4-6";
    [BindRequired]
    public AgentPreloadConfig PreloadConfig { get; set; }
    [BindRequired]
    public int DailyMessageCap { get; set; }
    [BindRequired]
    public int HourlyMessageCap { get; set; }
    [BindRequired]
    public int DailyTokenCap { get; set; }
    [BindRequired]
    public int RetentionDays { get; set; }
}
