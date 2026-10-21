using Microsoft.AspNetCore.Authorization;

namespace Humans.GoogleIntegration.Authorization;

/// <summary>
/// Resource-based requirement: may the user manage the Google resources of this team?
/// Used with <c>IAuthorizationService.AuthorizeAsync(User, teamInfo, TeamResourceManageRequirement.Instance)</c>
/// where the resource is a <c>TeamInfo</c>. Evaluated by <see cref="TeamResourceAuthorizationHandler"/>.
/// </summary>
internal sealed class TeamResourceManageRequirement : IAuthorizationRequirement
{
    public static readonly TeamResourceManageRequirement Instance = new();

    private TeamResourceManageRequirement()
    {
    }
}
