using Humans.Application.Interfaces.Containers;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Minimal shape <see cref="ContainerAuthorizationHandler"/> needs to make a
/// decision. Used in place of the <c>Container</c> entity so callers don't
/// hand-build sparse entities at the controller seam. Add fields here if the
/// handler grows to inspect more — that forces every callsite to provide them.
/// </summary>
public sealed record ContainerAuthorizationTarget(Guid CampId)
{
    public static ContainerAuthorizationTarget For(ContainerDto container) =>
        new(container.CampId);

    public static ContainerAuthorizationTarget ForCamp(Guid campId) =>
        new(campId);
}
