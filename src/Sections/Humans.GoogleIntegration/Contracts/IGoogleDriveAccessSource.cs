namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Implemented by each section that owns expected access to Drive resources.
/// GoogleIntegration hydrates user IDs, filters user state, unions multi-source claims,
/// and owns vendor mutations and their logs. An empty roster still claims the resource
/// so reconciliation can remove access after retirement.
/// </summary>
public interface IGoogleDriveAccessSource
{
    /// <summary>Returns every claimed Google file ID, or only the requested ID when supplied.</summary>
    Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default);
}

/// <summary>
/// Expected user access with optional owner-provided display metadata. <paramref name="ResourceName"/>
/// is the human-readable name of the folder for members' emails, the sync log and the sync page when no
/// registered Google resource names it; without one the raw file id shows.
/// </summary>
public sealed record GoogleDriveAccessClaim(
    Dictionary<Guid, DrivePermissionLevel> Access,
    IReadOnlyList<TeamLink>? LinkedTeams = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<TeamLink>>? MemberTeamLinks = null,
    string? ResourceName = null);
