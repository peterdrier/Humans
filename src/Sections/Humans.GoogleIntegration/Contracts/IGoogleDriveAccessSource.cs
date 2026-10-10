namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Implemented by each section that owns expected access to Drive resources.
/// GoogleIntegration hydrates user IDs, filters user state, detects source collisions,
/// and owns vendor mutations and their logs. An empty roster still claims the resource
/// so reconciliation can remove access after retirement.
/// </summary>
public interface IGoogleDriveAccessSource
{
    /// <summary>Returns every claimed Google file ID, or only the requested ID when supplied.</summary>
    Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default);
}

/// <summary>Expected user access with optional owner-provided links for sync display.</summary>
public sealed record GoogleDriveAccessClaim(
    Dictionary<Guid, DrivePermissionLevel> Access,
    IReadOnlyList<TeamLink>? LinkedTeams = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<TeamLink>>? MemberTeamLinks = null);
