using Microsoft.AspNetCore.Authorization;

namespace Humans.GoogleIntegration.Authorization;

/// <summary>The resource is a <c>TeamInfo</c>; see <see cref="TeamResourceAuthorizationHandler"/>.</summary>
internal sealed class TeamResourceManageRequirement : IAuthorizationRequirement
{
    public static readonly TeamResourceManageRequirement Instance = new();

    private TeamResourceManageRequirement()
    {
    }
}
