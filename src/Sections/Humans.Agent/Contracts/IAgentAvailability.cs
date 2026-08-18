namespace Humans.Agent.Contracts;

/// <summary>
/// Whether the agent feature is switched on. The only thing Shell asks the section: the help
/// widget shows the assistant panel when it is on, and two health checks skip their probes
/// when it is off.
/// </summary>
/// <remarks>
/// Deliberately narrower than the section's own settings service, which exposes the model id,
/// the rate-limit caps and the retention window — none of which anything outside the section
/// has ever read.
/// </remarks>
public interface IAgentAvailability
{
    bool IsEnabled { get; }
}
