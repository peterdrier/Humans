using Humans.Application.Interfaces;
using Humans.Domain.Enums;

namespace Humans.Web.Models.Agent;

/// <summary>
/// View model for <c>/Agent/Admin/Status</c>. Wraps the
/// <see cref="AgentAdminStatusReport"/> coming from the Application layer
/// together with the current settings DTO so the view can render the
/// system-state panel without a second service call.
/// </summary>
public sealed record AdminAgentStatusViewModel(
    AgentAdminStatusReport Report,
    AgentSettingsDto Settings)
{
    public bool Enabled => Settings.Enabled;
    public string Model => Settings.Model;
    public AgentPreloadConfig PreloadConfig => Settings.PreloadConfig;
}
