using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

internal interface ITeamResourceManagement : ITeamResourceService, IApplicationService
{
    /// <summary>The item must be on a Shared Drive shared with the service account as Editor.</summary>
    Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>The service account must be a Group Manager.</summary>
    Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Soft delete.</summary>
    Task UnlinkResourceAsync(Guid resourceId, Guid? actorUserId = null, CancellationToken ct = default);

    Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Also enforces the setting on Drive at once; the result carries any Drive failure.</summary>
    [ExternalWrite]
    Task<TeamResourceMutationResult> SetRestrictInheritedAccessWithResultAsync(Guid resourceId, bool restrict, Guid? actorUserId = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>Also clears any recorded sync error.</summary>
    Task MarkResourceSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default);

    Task RecordResourceErrorAsync(Guid resourceId, string errorMessage, CancellationToken ct = default);

    /// <summary>Includes inactive rows.</summary>
    Task<int> GetResourceCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes and audits each. Pass <paramref name="resourceType"/> when only that type was reconciled:
    /// deactivating unreconciled types would make the next sync skip them and leave Google access in place.
    /// </summary>
    Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default);

    /// <summary>Missing ids are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetResourceNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default);
}

internal sealed record TeamResourceMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamResourceMutationResult Success() => new(true, null);

    public static TeamResourceMutationResult Failed(string message) => new(false, message);
}
