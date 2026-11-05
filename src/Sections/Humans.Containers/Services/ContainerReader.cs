using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Domain;

namespace Humans.Containers.Services;

internal sealed class ContainerReader(
    IContainerRepository repo,
    ICampService campService) : IApplicationService
{
    public async Task<IReadOnlyList<ContainerDto>> GetByCampAsync(Guid campId, CancellationToken ct = default)
    {
        var containers = await repo.GetByCampAsync(campId, ct);
        return await ToDtosAsync(containers, ct);
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken ct = default)
    {
        var containers = await repo.GetAllAsync(ct);
        return await ToDtosAsync(containers, ct);
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var container = await repo.GetByIdAsync(id, ct);
        if (container is null) return null;
        return ToDto(container, await repo.GetImagesAsync([id], ct));
    }

    public async Task<IReadOnlyList<ContainerPlacementDto>> GetPlacementsByYearAsync(int year, CancellationToken ct = default)
    {
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        return placements.Select(ToPlacementDto).ToList();
    }

    public async Task<ContainerAdminOverview> GetAdminOverviewAsync(int year, CancellationToken ct = default)
    {
        var allContainers = await repo.GetAllAsync(ct);
        var placements = await repo.GetPlacementsByYearAsync(year, ct);
        var placementByContainerId = placements.ToDictionary(p => p.ContainerId, p => p);
        var imagesByContainerId = await LoadImagesAsync(allContainers, ct);

        var camps = await campService.GetCampsForYearAsync(year, ct);

        ContainerWithPlacement Compose(Container c) => new(
            ToDto(c, imagesByContainerId.GetValueOrDefault(c.Id, [])),
            placementByContainerId.TryGetValue(c.Id, out var p) ? ToPlacementDto(p) : null);

        var byCampId = allContainers
            .GroupBy(c => c.CampId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var campGroups = camps
            .Select(camp => new ContainerCampGroup(
                camp.Id,
                camp.Seasons.First(s => s.Year == year).Name,
                byCampId.TryGetValue(camp.Id, out var cs)
                    ? cs.Select(Compose).ToList()
                    : []))
            .ToList();

        return new ContainerAdminOverview(year, campGroups);
    }

    private async Task<IReadOnlyList<ContainerDto>> ToDtosAsync(
        IReadOnlyList<Container> containers, CancellationToken ct)
    {
        var imagesByContainerId = await LoadImagesAsync(containers, ct);
        return containers
            .Select(c => ToDto(c, imagesByContainerId.GetValueOrDefault(c.Id, [])))
            .ToList();
    }

    private async Task<Dictionary<Guid, List<ContainerImage>>> LoadImagesAsync(
        IReadOnlyList<Container> containers, CancellationToken ct)
    {
        var images = await repo.GetImagesAsync(containers.Select(c => c.Id).ToList(), ct);
        return images
            .GroupBy(i => i.ContainerId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    internal static ContainerDto ToDto(Container c, IReadOnlyList<ContainerImage> images)
    {
        var gallery = new List<ContainerImageDto>(images.Count + 1);
        // Legacy containers still hold their single image in the containers columns;
        // Guid.Empty addresses it so callers see one uniform, removable gallery.
        if (c.ImageStoragePath is not null)
        {
            gallery.Add(new ContainerImageDto(Guid.Empty, $"/{c.ImageStoragePath}", c.ImageFileName)
            { SortOrder = int.MinValue });
        }
        gallery.AddRange(images
            .Select(i => new ContainerImageDto(i.Id, $"/{i.StoragePath}", i.FileName) { SortOrder = i.SortOrder }));

        return new ContainerDto(c.Id, c.CampId, c.Name, c.Description, gallery, c.CreatedAt, c.UpdatedAt);
    }

    internal static ContainerPlacementDto ToPlacementDto(ContainerPlacement p) => new(
        p.ContainerId,
        p.Year,
        p.LocationGeoJson,
        p.PlacementNotes,
        p.PlacementImageStoragePath is not null ? $"/{p.PlacementImageStoragePath}" : null,
        p.PlacementImageFileName,
        p.CreatedAt,
        p.UpdatedAt);
}
