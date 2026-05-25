using Humans.Application.Interfaces.Teams;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Services.Teams;

// Shared TeamPageTeamSummary projection used by TeamService (Team entity) and CachingTeamService (TeamInfo).
public static class TeamPageSummaryMapper
{
    // DisplayName formula must match Team.DisplayName.
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
