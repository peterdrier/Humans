using Humans.Base.Constants;
using Humans.Shifts.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts.Authorization;

/// <summary>
/// Any team coordinator, a Shifts role holder, Board or Admin, without the privileged sub-panels behind
/// <see cref="ShiftsPolicies.Read"/>.
/// </summary>
internal sealed class IsAnyTeamManagerOrCoordinatorRequirement : IAuthorizationRequirement, IDescribedRequirement
{
    public IReadOnlyList<string> Admits => [RoleNames.Admin, RoleNames.Board, ShiftsRoles.ShiftsNoInfo, ShiftsRoles.ShiftsAdmin, "Team Coordinator"];
}
