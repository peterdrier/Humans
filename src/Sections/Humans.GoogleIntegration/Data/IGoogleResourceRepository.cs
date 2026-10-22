using Humans.GoogleIntegration.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.GoogleIntegration.Data;

/// <summary>Reads are <c>AsNoTracking</c>; each mutation loads, changes and saves in its own context.</summary>
internal interface IGoogleResourceRepository : IRepository
{
    Task<GoogleResource?> GetByIdAsync(Guid resourceId, CancellationToken ct = default);

    Task<IReadOnlyList<GoogleResource>> GetActiveByTeamIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Team ids with no active resources map to an empty list.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetActiveByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>Missing ids are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GoogleResource>> GetActiveByResourceTypeAsync(
        GoogleResourceType resourceType,
        CancellationToken ct = default);

    /// <summary>Includes inactive rows.</summary>
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>For duplicate-link prevention.</summary>
    Task<GoogleResource?> FindActiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>For reactivation on link. Drive resources match on the exact Google id.</summary>
    Task<GoogleResource?> FindInactiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>Case-insensitive match on <see cref="GoogleResource.GoogleId"/>.</summary>
    Task<GoogleResource?> FindActiveGroupByEmailAsync(
        Guid teamId,
        string normalizedGroupEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Legacy rows hold the group email instead of the numeric id in <see cref="GoogleResource.GoogleId"/>, so
    /// callers pass both candidates.
    /// </summary>
    Task<GoogleResource?> FindInactiveGroupByCandidatesAsync(
        Guid teamId,
        string googleNumericId,
        string normalizedGroupEmail,
        CancellationToken ct = default);

    Task AddAsync(GoogleResource resource, CancellationToken ct = default);

    /// <summary>Reactivates an inactive row and clears its error. Null if the row is missing.</summary>
    Task<GoogleResource?> ReactivateAsync(
        Guid resourceId,
        string name,
        string? url,
        Instant lastSyncedAt,
        string? newGoogleId,
        DrivePermissionLevel? newPermissionLevel,
        CancellationToken ct = default);

    Task UnlinkAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>False if the row is missing.</summary>
    Task<bool> UpdatePermissionLevelAsync(
        Guid resourceId,
        DrivePermissionLevel level,
        CancellationToken ct = default);

    /// <summary>Type checks and the Google-side change are the service's job. Null if the row is missing.</summary>
    Task<GoogleResource?> SetRestrictInheritedAccessAsync(
        Guid resourceId,
        bool restrict,
        CancellationToken ct = default);

    /// <summary>Returns the deactivated rows so callers can audit them.</summary>
    Task<IReadOnlyList<GoogleResource>> DeactivateByTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType,
        CancellationToken ct = default);

    // Narrow per-column mutations for GoogleWorkspaceSyncService: it holds no
    // tracked-entity graph, so the repo exposes an atomic update per field.

    /// <summary>Also clears <see cref="GoogleResource.ErrorMessage"/>.</summary>
    Task MarkSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default);

    /// <summary>Also clears <see cref="GoogleResource.ErrorMessage"/>. For Drive rows that share one Google id.</summary>
    Task MarkSyncedManyAsync(
        IReadOnlyCollection<Guid> resourceIds,
        Instant now,
        CancellationToken ct = default);

    Task SetErrorMessageManyAsync(
        IReadOnlyCollection<Guid> resourceIds,
        string errorMessage,
        CancellationToken ct = default);

    Task UpdateNameAsync(Guid resourceId, string name, CancellationToken ct = default);

    /// <summary>Writes no audit entry; the caller does.</summary>
    Task DeactivateAsync(Guid resourceId, CancellationToken ct = default);
}
