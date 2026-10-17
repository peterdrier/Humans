using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Full team-resource surface, and the sole owner of the <c>google_resources</c> table: callers
/// must never access <c>DbSet&lt;GoogleResource&gt;</c> directly. Other sections depend on
/// <see cref="ITeamResourceService"/> for reads and <see cref="ITeamResourceWrite"/> for the
/// link/unlink writes; this adds the sync bookkeeping and aggregates used inside the section.
/// </summary>
internal interface ITeamResourceManagement : ITeamResourceService, ITeamResourceWrite, IApplicationService
{
    /// <summary>
    /// Returns the total active-resource count for every team that currently has any,
    /// regardless of resource type. Used by admin aggregates (e.g. email rename impact).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks a Google resource reconciliation as successful and clears any
    /// previously recorded sync error.
    /// </summary>
    Task MarkResourceSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Records the last Google reconciliation error for a linked team resource.
    /// </summary>
    Task RecordResourceErrorAsync(Guid resourceId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Gets the active Google resources visible to a user, joined with their team metadata.
    /// Dashboard "My Google Resources" widget.
    /// </summary>
    Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of Google resource rows (including inactive).
    /// Used by the observable metrics gauge.
    /// </summary>
    Task<int> GetResourceCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Deactivates Google resources owned by a team (soft-delete: IsActive = false) and
    /// writes an audit log entry for each. Called by the Google reconciliation sync after
    /// a soft-deleted team's resources have had their access revoked, so the rows stop
    /// being processed by further reconciliation ticks.
    /// </summary>
    /// <param name="teamId">Team whose resources should be deactivated.</param>
    /// <param name="resourceType">
    /// If set, only resources of this type are deactivated. Required when the caller has
    /// only reconciled one resource type — deactivating the other types before they have
    /// been reconciled would cause the next per-type sync to skip them and leave Google
    /// access in place.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the display name for each resource id. Missing ids are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetResourceNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default);
}
