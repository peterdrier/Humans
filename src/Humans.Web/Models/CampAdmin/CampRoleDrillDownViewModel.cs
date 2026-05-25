namespace Humans.Web.Models.CampAdmin;

public sealed class CampRoleDrillDownViewModel
{
    public required string Slug { get; init; }
    /// <summary>
    /// The value used in the <c>{slug}</c> route segment for the form back-postback —
    /// the slug if non-empty, otherwise the role definition id. Per
    /// memory/architecture/slug-routes-fallback-to-guid.md, the route accepts either.
    /// </summary>
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

public sealed record CampRoleDrillDownCampRowViewModel(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleDrillDownAssigneeViewModel> Assignees);

public sealed record CampRoleDrillDownAssigneeViewModel(
    Guid UserId);
