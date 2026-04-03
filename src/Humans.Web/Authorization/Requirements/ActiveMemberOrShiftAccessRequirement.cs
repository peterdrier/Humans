using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Requirement satisfied when the user is an active member (has ActiveMember claim)
/// OR has shift dashboard access roles (Admin, NoInfoAdmin, VolunteerCoordinator)
/// OR is TeamsAdmin/Board/Admin.
/// Used for Shifts nav visibility.
/// </summary>
public class ActiveMemberOrShiftAccessRequirement : IAuthorizationRequirement;
