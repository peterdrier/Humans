using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

public record TeamResourceSummary(bool HasMailGroup, int DriveResourceCount)
{
    public static TeamResourceSummary Empty { get; } = new(false, 0);
}

public record GoogleResourceSnapshot(
    Guid Id,
    Guid TeamId,
    string GoogleId,
    string Name,
    GoogleResourceType ResourceType,
    string? Url,
    Instant ProvisionedAt = default,
    Instant? LastSyncedAt = null,
    bool IsActive = true,
    string? ErrorMessage = null,
    DrivePermissionLevel DrivePermissionLevel = DrivePermissionLevel.None,
    bool RestrictInheritedAccess = false);

public interface ITeamResourceService : IApplicationService
{
    /// <summary>Active only.</summary>
    Task<IReadOnlyList<GoogleResourceSnapshot>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Active only. A missing team id maps to an empty list.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>A missing team id maps to <see cref="TeamResourceSummary.Empty"/>.</summary>
    Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<GoogleResourceSnapshot>> GetActiveDriveFoldersAsync(CancellationToken ct = default);

    /// <summary>TeamsAdmin always can; coordinators only when AllowCoordinatorsToManageResources is on.</summary>
    Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);

    Task<GoogleResourceSnapshot?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default);
}
