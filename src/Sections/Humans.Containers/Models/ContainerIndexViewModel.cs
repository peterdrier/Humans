using Humans.Containers.Contracts;

namespace Humans.Containers.Models;

internal sealed class ContainerIndexViewModel
{
    public string CampSlug { get; set; } = string.Empty;
    public string CampName { get; set; } = string.Empty;
    public List<ContainerDto> Containers { get; set; } = [];
    public Dictionary<Guid, ContainerPlacementDto> PlacementsByContainerId { get; set; } = new();
    public int CurrentYear { get; set; }
    public bool IsLeadButPhaseClosed { get; set; }
}
