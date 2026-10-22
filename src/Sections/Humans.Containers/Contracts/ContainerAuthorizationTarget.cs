
namespace Humans.Containers.Contracts;

public sealed record ContainerAuthorizationTarget(Guid CampId)
{
    /// <summary>Placement year; null uses the current public year.</summary>
    public int? Year { get; init; }

    public static ContainerAuthorizationTarget For(ContainerDto container, int? year = null) =>
        new(container.CampId) { Year = year };
}
