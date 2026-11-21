using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

internal sealed record TeamTreeNode(
    string Name,
    string? Description,
    string Slug,
    int MemberCount,
    bool IsLinkable,
    IReadOnlyList<TeamTreeNode> Children);

internal static class TeamDirectoryBuilder
{
    /// <summary>
    /// Name-sorted at every level. Inactive, hidden and system teams are left out with everything under them. Anonymous
    /// viewers get only public-page departments; their sub-teams link only when promoted.
    /// </summary>
    public static IReadOnlyList<TeamTreeNode> BuildTree(
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        bool isAuthenticated)
    {
        var included = teamsById.Values
            .Where(t => t.IsActive && !t.IsHidden && !t.IsSystemTeam)
            .ToList();
        var childrenByParent = included
            .Where(t => t.ParentTeamId is not null)
            .ToLookup(t => t.ParentTeamId!.Value);

        return included
            .Where(t => t.ParentTeamId is null && (isAuthenticated || t.IsPublicPage))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => ToNode(t, childrenByParent, isAuthenticated))
            .ToList();
    }

    private static TeamTreeNode ToNode(TeamInfo team, ILookup<Guid, TeamInfo> childrenByParent, bool isAuthenticated) =>
        new(
            team.Name,
            team.Description,
            team.Slug,
            team.Members.Count,
            isAuthenticated || (team.ParentTeamId is null ? team.IsPublicPage : team.IsPromotedToDirectory),
            childrenByParent[team.Id]
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => ToNode(t, childrenByParent, isAuthenticated))
                .ToList());
}
