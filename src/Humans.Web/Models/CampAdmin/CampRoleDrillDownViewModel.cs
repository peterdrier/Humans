namespace Humans.Web.Models.CampAdmin;

public sealed class CampRoleDrillDownViewModel
{
    public required string Slug { get; init; }
    public required string RoleName { get; init; }
    public string? Description { get; init; }
    public int SlotCount { get; init; }
    public int MinimumRequired { get; init; }
    public int Year { get; init; }
    public required string GroupEmail { get; init; }
    public required IReadOnlyList<int> YearOptions { get; init; }
    public required IReadOnlyList<CampRoleDrillDownCampRowViewModel> Camps { get; init; }
}

public sealed record CampRoleDrillDownCampRowViewModel(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleDrillDownAssigneeViewModel> Assignees);

public sealed record CampRoleDrillDownAssigneeViewModel(
    Guid UserId,
    string DisplayName,
    string? GoogleEmail);
