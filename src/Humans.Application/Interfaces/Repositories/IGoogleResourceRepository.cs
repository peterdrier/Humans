using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>google_resources</c> table. The only non-test file
/// that may read or write that DbSet after the Teams migration (sub-task
/// <c>#540c</c>) lands.
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and
/// save changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context so callers never reason about EF context lifetime. No cross-domain
/// <c>Include</c>s: <c>TeamMembers</c>/<c>Teams</c> joins that the pre-migration
/// <c>TeamResourceService.GetUserTeamResourcesAsync</c> used are resolved at
/// the service layer via <see cref="Humans.Application.Interfaces.ITeamService"/>
/// per design-rules §2c/§6.
/// </remarks>
public interface IGoogleResourceRepository
{
    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Loads a single <see cref="GoogleResource"/> by id. Read-only (AsNoTracking).
    /// Returns null if not found.
    /// </summary>
    Task<GoogleResource?> GetByIdAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Returns all active resources for a team, ordered by
    /// <see cref="GoogleResource.ProvisionedAt"/>. Read-only.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> GetActiveByTeamIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Returns active resources grouped by team id, for a set of team ids.
    /// Missing team ids map to an empty list in the returned dictionary.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetActiveByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the total active-resource count for every team that currently has
    /// at least one active resource. Read-only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every active resource of type <see cref="GoogleResourceType.DriveFolder"/>
    /// across all teams. Read-only.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of resource rows (including inactive). Used by
    /// the observable metrics gauge. Read-only.
    /// </summary>
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Finds an existing active resource matching the given
    /// (team, Google id, type) tuple, for duplicate-link prevention. Read-only.
    /// </summary>
    Task<GoogleResource?> FindActiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>
    /// Finds an existing inactive resource for reactivation during a link
    /// operation (Drive folders/files match on exact Google id). Read-only.
    /// </summary>
    Task<GoogleResource?> FindInactiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>
    /// Finds an existing active Google-Group resource for a team by group email
    /// (case-insensitive match on <see cref="GoogleResource.GoogleId"/>).
    /// Read-only.
    /// </summary>
    Task<GoogleResource?> FindActiveGroupByEmailAsync(
        Guid teamId,
        string normalizedGroupEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Finds an existing inactive Google-Group resource for reactivation. The
    /// stored <see cref="GoogleResource.GoogleId"/> may contain either the raw
    /// numeric id or the original email (legacy rows), so callers pass both
    /// candidates. Case-insensitive email match. Read-only.
    /// </summary>
    Task<GoogleResource?> FindInactiveGroupByCandidatesAsync(
        Guid teamId,
        string googleNumericId,
        string normalizedGroupEmail,
        CancellationToken ct = default);

    // ==========================================================================
    // Writes
    // ==========================================================================

    /// <summary>
    /// Inserts a new <see cref="GoogleResource"/> row.
    /// </summary>
    Task AddAsync(GoogleResource resource, CancellationToken ct = default);

    /// <summary>
    /// Applies a reactivation update to an existing (inactive) row: sets
    /// <c>Name</c>, <c>Url</c>, <c>LastSyncedAt</c>, clears <c>ErrorMessage</c>,
    /// sets <c>IsActive = true</c>, and optionally updates
    /// <c>GoogleId</c>/<c>DrivePermissionLevel</c>. No-op if the row is missing.
    /// Returns the reactivated entity (fresh read-only snapshot) or null.
    /// </summary>
    Task<GoogleResource?> ReactivateAsync(
        Guid resourceId,
        string name,
        string? url,
        Instant lastSyncedAt,
        string? newGoogleId,
        DrivePermissionLevel? newPermissionLevel,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="GoogleResource.IsActive"/> to false on a single row.
    /// No-op if the row is missing.
    /// </summary>
    Task UnlinkAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Applies a new <see cref="DrivePermissionLevel"/> to a single row.
    /// No-op if the row is missing.
    /// </summary>
    /// <returns>True if the row existed and was updated; false otherwise.</returns>
    Task<bool> UpdatePermissionLevelAsync(
        Guid resourceId,
        DrivePermissionLevel level,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a new <see cref="GoogleResource.RestrictInheritedAccess"/> flag
    /// to a single row. Returns the row's state after mutation (type check and
    /// Google-side enforcement are the service's responsibility), or null if
    /// the row did not exist.
    /// </summary>
    Task<GoogleResource?> SetRestrictInheritedAccessAsync(
        Guid resourceId,
        bool restrict,
        CancellationToken ct = default);

    /// <summary>
    /// Load-and-deactivate helper for bulk soft-delete flows. Flips
    /// <c>IsActive = false</c> on every active resource matching
    /// <paramref name="teamId"/> (and optional <paramref name="resourceType"/>
    /// filter) and persists atomically. Returns the entities that were
    /// deactivated so callers can feed audit logs.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> DeactivateByTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType,
        CancellationToken ct = default);
}
