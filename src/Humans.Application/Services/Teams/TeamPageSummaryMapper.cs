using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Single source of truth for projecting a team + its parent into a
/// <see cref="TeamPageTeamSummary"/>. Both <see cref="TeamService"/> (inner,
/// works from the EF-loaded <see cref="Team"/> entity) and
/// <c>CachingTeamService</c> (decorator, works from <see cref="TeamInfo"/>
/// cache projections) project to the same record shape; this helper exists
/// so the positional record constructor (17 fields) is built in exactly one
/// place, eliminating drift risk between the two paths.
/// </summary>
public static class TeamPageSummaryMapper
{
    /// <summary>
    /// Builds a <see cref="TeamPageTeamSummary"/> from the canonical scalar
    /// inputs. The display-name formula must match <see cref="Team.DisplayName"/>:
    /// <c>parent is not null ? $"{parent.Name} - {team.Name}" : team.Name</c>.
    /// </summary>
    public static TeamPageTeamSummary Map(
        Guid id,
        string name,
        string? parentName,
        string? description,
        string slug,
        bool isActive,
        bool requiresApproval,
        bool isSystemTeam,
        SystemTeamType systemTeamType,
        Instant createdAt,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        string? pageContent,
        List<CallToAction> callsToAction,
        Instant? pageContentUpdatedAt,
        Guid? pageContentUpdatedByUserId,
        TeamPageTeamLink? parentLink)
    {
        var displayName = parentName is not null ? $"{parentName} - {name}" : name;
        return new TeamPageTeamSummary(
            id,
            name,
            displayName,
            description,
            slug,
            isActive,
            requiresApproval,
            isSystemTeam,
            systemTeamType,
            createdAt,
            isPublicPage,
            showCoordinatorsOnPublicPage,
            pageContent,
            callsToAction,
            pageContentUpdatedAt,
            pageContentUpdatedByUserId,
            parentLink);
    }
}
