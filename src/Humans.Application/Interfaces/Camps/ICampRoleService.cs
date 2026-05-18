using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Camps;

public interface ICampRoleService : IApplicationService
{
    // Definitions

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

    Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<UpdateCampRoleDefinitionResult> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    // Per-camp assignments

    Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<AssignCampRoleOutcome> AssignAsync(Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Loads a single assignment (including its season) so callers can verify
    /// camp ownership before mutating it. Used by the per-camp UnassignRole
    /// controller action for the C2 cross-camp ownership check.
    /// </summary>
    Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Cascade hook — deletes every role assignment for the given camp member.
    /// Called by <see cref="ICampService.LeaveCampAsync"/> and
    /// <see cref="ICampService.WithdrawCampMembershipRequestAsync"/>.
    /// </summary>
    Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    // Reporting

    Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Idempotent admin action: ensures the Camp Lead and Events Lead system role
    /// definitions exist (matched by <see cref="CampSystemRoles"/> constants and
    /// <c>IsSystem = true</c>), then walks the legacy <c>camp_leads</c> table and
    /// creates a <see cref="CampRoleAssignment"/> for the Camp Lead role on the
    /// camp's open (or most-recent-open) season, creating
    /// <see cref="CampMember"/>(<c>Status = Active</c>) as needed. Camps with no
    /// season are skipped and logged. Safe to run multiple times — the second
    /// run is a no-op for already-seeded definitions and already-migrated leads.
    /// </summary>
    Task<SeedSystemRolesResult> SeedSystemRolesAndMigrateLeadsAsync(Guid actorUserId, CancellationToken ct = default);
}

/// <summary>
/// Well-known identifiers for system-managed camp role definitions.
/// Names + slugs are the stable match key (rows are looked up by name + IsSystem,
/// not by GUID, because seeding generates a fresh GUID per environment).
/// </summary>
public static class CampSystemRoles
{
    public const string CampLeadName = "Camp Lead";
    public const string CampLeadSlug = "camp-lead";
    public const int CampLeadSortOrder = 0;
    public const int CampLeadSlotCount = 2;
    public const int CampLeadMinimumRequired = 1;

    public const string EventsLeadName = "Events Lead";
    public const string EventsLeadSlug = "events-lead";
    public const int EventsLeadSortOrder = 10;
    public const int EventsLeadSlotCount = 2;
    public const int EventsLeadMinimumRequired = 0;
}

public sealed record SeedSystemRolesResult(
    int DefinitionsCreated,
    int LeadsMigrated,
    int LeadsAlreadyMigrated,
    IReadOnlyList<string> SkippedCampSlugs)
{
    public int LeadsSkipped => SkippedCampSlugs.Count;
}

public sealed record CreateCampRoleDefinitionInput(
    string Name,
    string Slug,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

public sealed record UpdateCampRoleDefinitionInput(
    string Name,
    string Slug,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

public enum UpdateCampRoleDefinitionStatus
{
    Updated,
    NotFound,
}

public sealed record UpdateCampRoleDefinitionResult(UpdateCampRoleDefinitionStatus Status, string SuccessMessage)
{
    public static UpdateCampRoleDefinitionResult Updated(string name) =>
        new(UpdateCampRoleDefinitionStatus.Updated, $"Updated camp role '{name}'.");

    public static UpdateCampRoleDefinitionResult NotFound { get; } =
        new(UpdateCampRoleDefinitionStatus.NotFound, string.Empty);
}

public sealed record CampRoleDefinitionInfo(
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
    bool IsSystem)
{
    public bool IsActive => DeactivatedAt is null;
}

public sealed record CampRoleAssignmentInfo(
    Guid Id,
    Guid CampSeasonId,
    Guid CampRoleDefinitionId,
    Guid CampMemberId);
