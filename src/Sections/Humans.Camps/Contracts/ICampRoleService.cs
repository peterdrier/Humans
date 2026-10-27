using NodaTime;

namespace Humans.Camps.Contracts;

internal interface ICampRoleService : IApplicationService
{
    Task<IReadOnlyList<CampRoleDefinitionInfo>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinitionInfo?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

    Task<CampRoleDefinitionInfo?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Builds the deterministic Google Group key for a (role-definition slug, season year) pair:
    /// <c>barrios-{year}-{slug}@{domain}</c>. Caller is responsible for guarding empty slugs.
    /// </summary>
    string BuildGroupKey(int year, string slug);

    /// <summary>
    /// Builds the cross-camp roster for one role definition in a given year. One row per
    /// camp-season participating in <paramref name="year"/>, with assignees (name + Google email)
    /// or an empty state. Read-only; caller authorizes.
    /// </summary>
    Task<CampRoleDrillDownData?> BuildDrillDownAsync(Guid roleDefinitionId, int year, CancellationToken ct = default);

    Task<CampAdminMutationResult> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<UpdateCampRoleDefinitionResult> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<AssignCampRoleOutcome> AssignAsync(Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Includes its season, so callers can verify camp ownership before mutating it.</summary>
    Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Cascade hook for leaving a camp or withdrawing a membership request.</summary>
    Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Per-season fill summary across all active role definitions, keyed by <c>CampSeasonId</c>.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>>
        GetDirectoryRoleSummariesAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Assignees join by <see cref="CampRoleAssignment.CampRoleDefinitionId"/>, never by role name, which breaks
    /// silently on rename. Read-only; caller authorizes.
    /// </summary>
    Task<CampComplianceMatrixData> BuildComplianceMatrixAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Idempotent: ensures every non-<see cref="CampSpecialRole.None"/> special role has a system role definition.
    /// Returns the number created.
    /// </summary>
    Task<int> SeedSystemRolesAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>The admin "Seed system roles" button hides when this is empty.</summary>
    Task<IReadOnlyList<CampSpecialRole>> GetMissingSpecialRolesAsync(CancellationToken ct = default);
}

/// <summary>
/// Seeding defaults, looked up by <see cref="CampRoleDefinition.SpecialRole"/>. Admins may not rename or re-slug a
/// seeded special-role row.
/// </summary>
internal static class CampSystemRoles
{
    public const string CampLeadName = "Camp Lead";
    public const string CampLeadSlug = "camp-lead";
    public const int CampLeadSortOrder = -1000;
    public const int CampLeadSlotCount = 2;
    public const int CampLeadMinimumRequired = 1;

    public const string WorkshopLeadName = "Workshop Lead";
    public const string WorkshopLeadSlug = "workshop-lead";
    public const int WorkshopLeadSortOrder = -990;
    public const int WorkshopLeadSlotCount = 2;
    public const int WorkshopLeadMinimumRequired = 0;
}

internal sealed record CreateCampRoleDefinitionInput(
    string Name,
    string Slug,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

internal sealed record UpdateCampRoleDefinitionInput(
    string Name,
    string Slug,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

internal enum UpdateCampRoleDefinitionStatus
{
    Updated,
    NotFound,
    Rejected,
}

internal sealed record UpdateCampRoleDefinitionResult(UpdateCampRoleDefinitionStatus Status, string SuccessMessage, string? ErrorMessage = null)
{
    public static UpdateCampRoleDefinitionResult Updated(string name) =>
        new(UpdateCampRoleDefinitionStatus.Updated, $"Updated camp role '{name}'.");

    public static UpdateCampRoleDefinitionResult Rejected(string errorMessage) =>
        new(UpdateCampRoleDefinitionStatus.Rejected, string.Empty, errorMessage);

    public static UpdateCampRoleDefinitionResult NotFound { get; } =
        new(UpdateCampRoleDefinitionStatus.NotFound, string.Empty);
}

internal sealed record CampRoleDefinitionInfo(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? DeactivatedAt,
    CampSpecialRole SpecialRole)
{
    public bool IsActive => DeactivatedAt is null;
    public bool IsSpecial => SpecialRole != CampSpecialRole.None;
}

internal sealed record CampRoleAssignmentInfo(
    Guid Id,
    Guid CampSeasonId,
    Guid CampRoleDefinitionId,
    Guid CampMemberId);
