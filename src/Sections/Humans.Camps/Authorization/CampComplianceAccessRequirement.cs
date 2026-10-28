using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Authorization;

/// <summary>
/// CampAdmin, Admin, or any team or sub-team coordinator: opens the read-only compliance matrix without widening the
/// CampAdmin-only management surface.
/// </summary>
internal sealed class CampComplianceAccessRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [CampsRoles.CampsAdmin, RoleNames.Admin, "Team Coordinator"];
}
