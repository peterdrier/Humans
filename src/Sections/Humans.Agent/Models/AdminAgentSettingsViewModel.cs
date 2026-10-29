using Humans.Agent.Domain;
using Humans.Agent.Services;
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
    /// <summary>Display only; never bound from the form.</summary>
    [BindNever]
    public AgentModelOptions ModelOptions { get; set; } = new([], LiveListAvailable: false);
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
