
namespace Humans.Containers.Contracts;

/// <summary>
/// Minimal shape <c>ContainerAuthorizationHandler</c> needs to make a
/// decision. Used in place of the <c>Container</c> entity so callers don't
/// hand-build sparse entities at the controller seam.
/// </summary>
public sealed record ContainerAuthorizationTarget(Guid CampId)
{
    public static ContainerAuthorizationTarget For(ContainerDto container) =>
        new(container.CampId);

    public static ContainerAuthorizationTarget ForCamp(Guid campId) =>
        new(campId);
}
