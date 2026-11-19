using Humans.Google.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Google.Data;

/// <summary>Reads are <c>AsNoTracking</c>; each mutation loads, changes and saves in its own context.</summary>
internal interface IGoogleResourceRepository : IRepository
{
    Task<TeamGoogleResource?> GetByIdAsync(Guid resourceId, CancellationToken ct = default);

    Task<IReadOnlyList<TeamGoogleResource>> GetActiveByTeamIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Team ids with no active resources map to an empty list.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TeamGoogleResource>>> GetActiveByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>Missing ids are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TeamGoogleResource>> GetActiveByResourceTypeAsync(
        GoogleResourceType resourceType,
        CancellationToken ct = default);

    /// <summary>Includes inactive rows.</summary>
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>For duplicate-link prevention.</summary>
    Task<TeamGoogleResource?> FindActiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>For reactivation on link. Drive resources match on the exact Google id.</summary>
    Task<TeamGoogleResource?> FindInactiveByGoogleIdAsync(
        Guid teamId,
        string googleId,
        GoogleResourceType type,
        CancellationToken ct = default);

    /// <summary>Case-insensitive match on <see cref="TeamGoogleResource.GoogleId"/>.</summary>
    Task<TeamGoogleResource?> FindActiveGroupByEmailAsync(
        Guid teamId,
        string normalizedGroupEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Legacy rows hold the group email instead of the numeric id in <see cref="TeamGoogleResource.GoogleId"/>, so
    /// callers pass both candidates. A canonical URL also identifies a numeric-ID row during team-prefix recovery.
    /// </summary>
    Task<TeamGoogleResource?> FindInactiveGroupByCandidatesAsync(
        Guid teamId,
        string googleNumericId,
        string normalizedGroupEmail,
        string? normalizedGroupUrl = null,
        CancellationToken ct = default);

    Task AddAsync(TeamGoogleResource resource, CancellationToken ct = default);

    /// <summary>Reactivates a row and optionally deactivates a previous same-team/type row in one save. Null if the target is missing.</summary>
    Task<TeamGoogleResource?> ReactivateAsync(
        Guid resourceId,
        string name,
        string? url,
        Instant lastSyncedAt,
        string? newGoogleId,
        DrivePermissionLevel? newPermissionLevel,
        Guid? deactivateResourceId = null,
        CancellationToken ct = default);

    Task UnlinkAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>False if the row is missing.</summary>
    Task<bool> UpdatePermissionLevelAsync(
        Guid resourceId,
        DrivePermissionLevel level,
        CancellationToken ct = default);

    /// <summary>Type checks and the Google-side change are the service's job. Null if the row is missing.</summary>
    Task<TeamGoogleResource?> SetRestrictInheritedAccessAsync(
        Guid resourceId,
        bool restrict,
        CancellationToken ct = default);

    /// <summary>Returns the deactivated rows so callers can audit them.</summary>
    Task<IReadOnlyList<TeamGoogleResource>> DeactivateByTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType,
        CancellationToken ct = default);

    // Narrow per-column mutations for GoogleWorkspaceSyncService: it holds no
    // tracked-entity graph, so the repo exposes an atomic update per field.

    /// <summary>Also clears <see cref="TeamGoogleResource.ErrorMessage"/>.</summary>
    Task MarkSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default);

    /// <summary>Also clears <see cref="TeamGoogleResource.ErrorMessage"/>. For Drive rows that share one Google id.</summary>
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
