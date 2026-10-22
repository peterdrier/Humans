using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts.Authorization;

/// <summary>
/// Any team coordinator or management-role holder, without the privileged sub-panels behind
/// <see cref="PolicyNames.ShiftDashboardAccess"/>.
/// </summary>
internal sealed class IsAnyTeamManagerOrCoordinatorRequirement : IAuthorizationRequirement;
