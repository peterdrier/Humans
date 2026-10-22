using Humans.Agent.Domain;
using Humans.Agent.Services;

namespace Humans.Agent.Models;

internal sealed record AdminAgentStatusViewModel(
    AgentAdminStatusReport Report,
    AgentSettingsDto Settings)
{
    public bool Enabled => Settings.Enabled;
    public string Model => Settings.Model;
    public AgentPreloadConfig PreloadConfig => Settings.PreloadConfig;
}
