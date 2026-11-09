using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;

namespace Humans.GoogleIntegration.Services;

internal sealed class TeamResourceReader(IGoogleResourceRepository repository) : IApplicationService
{
    internal async Task<IReadOnlyList<GoogleResourceSnapshot>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        var resources = await repository.GetActiveByTeamIdAsync(teamId, ct);
        return resources.Select(ToSnapshot).ToList();
    }

    internal async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        var resourcesByTeam = await repository.GetActiveByTeamIdsAsync(teamIds, ct);
        return resourcesByTeam.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<GoogleResourceSnapshot>)kvp.Value.Select(ToSnapshot).ToList());
    }

    internal async Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, TeamResourceSummary>();
        }

        var rowsByTeam = await repository.GetActiveByTeamIdsAsync(teamIds, ct);

        var result = new Dictionary<Guid, TeamResourceSummary>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            var rows = rowsByTeam[teamId];
            if (rows.Count == 0)
            {
                result[teamId] = TeamResourceSummary.Empty;
                continue;
            }
            var hasMailGroup = rows.Any(r => r.ResourceType == GoogleResourceType.Group);
            var driveCount = rows.Count(r => r.ResourceType != GoogleResourceType.Group);
            result[teamId] = new TeamResourceSummary(hasMailGroup, driveCount);
        }
        return result;
    }

    internal Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default)
        => repository.GetActiveResourceCountsByTeamAsync(ct);

    internal async Task<IReadOnlyList<GoogleResourceSnapshot>> GetActiveDriveFoldersAsync(CancellationToken ct = default)
    {
        var resources = await repository.GetActiveByResourceTypeAsync(GoogleResourceType.DriveFolder, ct);
        return resources.Select(ToSnapshot).ToList();
    }

    internal Task<int> GetResourceCountAsync(CancellationToken ct = default)
        => repository.GetCountAsync(ct);

    internal Task<IReadOnlyDictionary<Guid, string>> GetResourceNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default)
        => repository.GetNamesByIdsAsync(resourceIds, ct);

    internal async Task<GoogleResourceSnapshot?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await repository.GetByIdAsync(resourceId, ct);
        return resource is null ? null : ToSnapshot(resource);
    }

    private static GoogleResourceSnapshot ToSnapshot(GoogleResource resource) =>
        new(
            resource.Id,
            resource.TeamId,
            resource.GoogleId,
            resource.Name,
            resource.ResourceType,
            resource.Url,
            resource.ProvisionedAt,
            resource.LastSyncedAt,
            resource.IsActive,
            resource.ErrorMessage,
            resource.DrivePermissionLevel,
            resource.RestrictInheritedAccess);

}
