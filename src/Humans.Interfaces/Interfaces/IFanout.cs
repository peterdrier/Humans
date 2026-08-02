namespace Humans.Application.Interfaces;

/// <summary>
/// Terminology marker for a fan-out contributor contract: an interface many
/// sections implement and a single coordinator (orchestrator) aggregates.
/// Examples: <c>IUserDataContributor</c> (GDPR export) and
/// <c>IEarlyEntryProvider</c> (early-entry roster). No analyzer — this marker
/// exists for searchability and to name the fan-out seam. Contract purity
/// (read-only, DTO-not-entity returns) is enforced elsewhere.
/// </summary>
public interface IFanout
{
}
