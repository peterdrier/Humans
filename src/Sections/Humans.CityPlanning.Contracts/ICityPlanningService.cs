using Humans.Base.Interfaces;

namespace Humans.CityPlanning.Contracts;

public interface ICityPlanningService : IApplicationService
{
    /// <summary>
    /// Whether barrio leads may place containers in <paramref name="year"/>. A year with no settings row reads as
    /// closed.
    /// </summary>
    Task<bool> IsContainerPlacementOpenAsync(int year, CancellationToken cancellationToken = default);

    /// <summary>
    /// The registration info City Planning stored for <paramref name="year"/> before Camps took it over. Read only
    /// by Camps' <c>camps.registration-info-from-city-planning</c> data migration and its Register page fallback;
    /// both go in that migration's retirement PR.
    /// </summary>
    Task<string?> GetLegacyRegistrationInfoAsync(int year, CancellationToken cancellationToken = default);

    Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default);
}
