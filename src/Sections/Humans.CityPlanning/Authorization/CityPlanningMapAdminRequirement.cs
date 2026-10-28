using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.CityPlanning.Authorization;

/// <summary>
/// The same audience <c>CityPlanningController.RequireMapAdminAsync</c> admits, so the <c>/Settings#city-planning</c>
/// tab shows to exactly the people its POST endpoints accept.
/// </summary>
internal sealed class CityPlanningMapAdminRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [CampsRoles.CampsAdmin, RoleNames.Admin, "City Planning team"];
}
