namespace Humans.Web.Models.CampAdmin;

public sealed class CampRoleComplianceViewModel
{
    public required int Year { get; init; }
    public required IReadOnlyList<CampRoleComplianceCampRowViewModel> Camps { get; init; }
}

public sealed record CampRoleComplianceCampRowViewModel(
    Guid CampId, string CampName, string CampSlug, Guid CampSeasonId,
    IReadOnlyList<CampRoleComplianceRoleRowViewModel> Roles, bool IsCompliant);

public sealed record CampRoleComplianceRoleRowViewModel(
    string DefinitionName, int MinimumRequired, int Filled, bool IsMet);
