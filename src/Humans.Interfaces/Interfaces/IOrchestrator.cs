namespace Humans.Application.Interfaces;

/// <summary>
/// Marker for an Orchestrator: a service that coordinates ≥2 sections
/// through their public service interfaces, owns no tables, and injects no
/// repository. SIBLING of <see cref="IApplicationService"/>, never a child —
/// <see cref="IApplicationService"/> grants own-lane repository access, which
/// an orchestrator is defined not to have. A service is one or the other,
/// never both (HUM0027). Orchestrators may not inject any
/// <c>I*Repository</c> or <c>HumansDbContext</c> (HUM0026). See
/// <c>memory/architecture/orchestrator-marker.md</c>.
/// </summary>
public interface IOrchestrator
{
}
