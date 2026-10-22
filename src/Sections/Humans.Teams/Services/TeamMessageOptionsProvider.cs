using Humans.Base.Constants;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Active teams the viewer coordinates whose Google Group is linked and synced. The profile buttons render this list
/// and <c>TeamMessageController</c> authorizes against the same one.
/// </summary>
internal interface ITeamMessageOptionsProvider
{
    Task<IReadOnlyList<TeamMessageOption>> GetOptionsAsync(Guid viewerId, CancellationToken ct = default);
}

internal sealed record TeamMessageOption(Guid TeamId, string TeamName, string GoogleGroupEmail);

internal sealed class TeamMessageOptionsProvider(
    ITeamService teamService,
    ITeamResourceService teamResourceService) : ITeamMessageOptionsProvider
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

    // Url is the one field both GoogleIntegration write paths store the same way and key their duplicate checks on.
    // Name differs per path and GoogleId is Google's numeric id, so neither identifies the group on every row.
    private static string GroupUrl(string prefix) =>
        $"https://groups.google.com/a/{DomainConstants.GoogleGroupDomain}/g/{prefix}";

    private static bool IsSyncedGroup(GoogleResourceSnapshot resource, string groupUrl) =>
        resource.ResourceType == GoogleResourceType.Group
        && resource.IsActive
        && resource.LastSyncedAt is not null
        && resource.ErrorMessage is null
        && string.Equals(resource.Url?.TrimEnd('/'), groupUrl, StringComparison.OrdinalIgnoreCase);
}
