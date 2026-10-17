using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Models;

namespace Humans.Users.Services;

/// <summary>
/// The teams a viewer may send a facilitated message on behalf of: active teams the viewer
/// coordinates whose Google Group is linked and synced. The profile page renders the list as
/// buttons and the send endpoints authorize a requested team by looking it up in the same list.
/// </summary>
internal interface ITeamMessageOptionsProvider
{
    Task<IReadOnlyList<TeamMessageOption>> GetOptionsAsync(Guid viewerId, CancellationToken ct = default);
}

// Owns no tables: coordinates Teams and GoogleIntegration through their public interfaces.
internal sealed class TeamMessageOptionsProvider(
    ITeamServiceRead teamService,
    ITeamResourceService teamResourceService) : ITeamMessageOptionsProvider, IOrchestrator
{
    public async Task<IReadOnlyList<TeamMessageOption>> GetOptionsAsync(Guid viewerId, CancellationToken ct = default)
    {
        var coordinatedTeams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive
                && t.GoogleGroupPrefix is not null
                && t.Members.Any(m => m.UserId == viewerId && m.Role == TeamMemberRole.Coordinator))
            .ToList();
        if (coordinatedTeams.Count == 0)
            return [];

        var resourcesByTeam = await teamResourceService.GetResourcesByTeamIdsAsync(
            coordinatedTeams.Select(t => t.Id).ToList(), ct);

        return coordinatedTeams
            .Where(t => resourcesByTeam.GetValueOrDefault(t.Id, [])
                .Any(r => IsSyncedGroup(r, GroupUrl(t.GoogleGroupPrefix!))))
            .Select(t => new TeamMessageOption(t.Id, t.Name, t.GoogleGroupEmail!))
            .ToList();
    }

    // Url is the one field both GoogleIntegration write paths (GoogleWorkspaceSyncService
    // provisioning and TeamResourceService.LinkGroupAsync) store as
    // https://groups.google.com/a/{domain}/g/{prefix}, and the one they key their own
    // duplicate checks on. Name differs per path (team name vs group email) and GoogleId is
    // Google's numeric id, so neither identifies the group on every row.
    private static string GroupUrl(string prefix) =>
        $"https://groups.google.com/a/{DomainConstants.GoogleGroupDomain}/g/{prefix}";

    private static bool IsSyncedGroup(GoogleResourceSnapshot resource, string groupUrl) =>
        resource.ResourceType == GoogleResourceType.Group
        && resource.IsActive
        && resource.LastSyncedAt is not null
        && resource.ErrorMessage is null
        && string.Equals(resource.Url?.TrimEnd('/'), groupUrl, StringComparison.OrdinalIgnoreCase);
}
