using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Authorization;

/// <summary>
/// Camps Admin, Board, Admin, or any team or sub-team coordinator: opens the read-only compliance matrix without
/// widening the Camps Admin management surface.
/// </summary>
internal sealed class CampComplianceAccessRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [CampsRoles.CampsAdmin, RoleNames.Board, RoleNames.Admin, "Team Coordinator"];
}
