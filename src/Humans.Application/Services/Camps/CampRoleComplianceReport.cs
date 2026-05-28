namespace Humans.Application.Services.Camps;

public sealed record CampRoleComplianceReport(
    int Year,
    IReadOnlyList<CampRoleComplianceCampRow> Camps);

public sealed record CampRoleComplianceCampRow(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleComplianceRoleRow> Roles,
    bool IsCompliant);

public sealed record CampRoleComplianceRoleRow(
    Guid DefinitionId,
    string DefinitionName,
    int MinimumRequired,
    int Filled,
    bool IsMet);

/// <summary>
/// Per-season fill count for a single role definition, used by the /Barrios
/// directory "show lead positions" pills. Unlike <see cref="CampRoleComplianceRoleRow"/>
/// this covers ALL active definitions (not just <c>MinimumRequired &gt; 0</c>) and
/// uses <see cref="SlotCount"/> as the denominator.
/// </summary>
public sealed record CampDirectoryRoleSummary(
    string Name,
    int Filled,
    int SlotCount);
