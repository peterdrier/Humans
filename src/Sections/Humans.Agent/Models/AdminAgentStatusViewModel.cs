using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Agent.Domain;
using Humans.Agent.Services;

namespace Humans.Agent.Models;

/// <summary>
/// View model for <c>/Agent/Admin/Status</c>. Wraps the
/// <see cref="AgentAdminStatusReport"/> coming from the Application layer
/// together with the current settings DTO so the view can render the
/// system-state panel without a second service call.
/// </summary>
internal sealed record AdminAgentStatusViewModel(
    AgentAdminStatusReport Report,
    AgentSettingsDto Settings)
{
    public bool Enabled => Settings.Enabled;
    public string Model => Settings.Model;
    public AgentPreloadConfig PreloadConfig => Settings.PreloadConfig;
}
