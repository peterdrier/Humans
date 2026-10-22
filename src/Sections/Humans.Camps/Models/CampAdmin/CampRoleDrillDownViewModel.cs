namespace Humans.Camps.Models;

internal sealed class CampRoleDrillDownViewModel
{
    public required string Slug { get; init; }
    /// <summary>The slug if non-empty, otherwise the definition id: the route accepts either.</summary>
    public required string RouteKey { get; init; }
    public required string RoleName { get; init; }
    public string? Description { get; init; }
    public int SlotCount { get; init; }
    public int MinimumRequired { get; init; }
    public int Year { get; init; }
    public string? GroupEmail { get; init; }
    public required IReadOnlyList<int> YearOptions { get; init; }
    public required IReadOnlyList<CampRoleDrillDownCampRowViewModel> Camps { get; init; }
}

internal sealed record CampRoleDrillDownCampRowViewModel(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleDrillDownAssigneeViewModel> Assignees);

internal sealed record CampRoleDrillDownAssigneeViewModel(
    Guid UserId);
