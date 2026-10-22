namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// GoogleIntegration hydrates the ids, filters user state, unions claims and owns the mutations. An empty roster
/// still claims the resource, so reconciliation can remove access after retirement.
/// </summary>
public interface IGoogleDriveAccessSource
{
    /// <summary>Returns every claimed Google file ID, or only the requested ID when supplied.</summary>
    Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="ResourceName"/> names the folder in emails, the sync log and the sync page when no registered
/// resource does; without one the raw file id shows.
/// </summary>
public sealed record GoogleDriveAccessClaim(
    Dictionary<Guid, DrivePermissionLevel> Access,
    IReadOnlyList<TeamLink>? LinkedTeams = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<TeamLink>>? MemberTeamLinks = null,
    string? ResourceName = null);
