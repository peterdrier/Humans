
namespace Humans.Containers.Contracts;

/// <summary>
/// Minimal shape <c>ContainerAuthorizationHandler</c> needs to make a
/// decision. Used in place of the <c>Container</c> entity so callers don't
/// hand-build sparse entities at the controller seam.
/// </summary>
public sealed record ContainerAuthorizationTarget(Guid CampId)
{
    /// <summary>Placement year; null uses the current public year.</summary>
    public int? Year { get; init; }

    public static ContainerAuthorizationTarget For(ContainerDto container, int? year = null) =>
        new(container.CampId) { Year = year };

    public static ContainerAuthorizationTarget ForCamp(Guid campId) =>
        new(campId);
}
