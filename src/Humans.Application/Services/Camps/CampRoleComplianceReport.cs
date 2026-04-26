using Humans.Domain.Entities;

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
