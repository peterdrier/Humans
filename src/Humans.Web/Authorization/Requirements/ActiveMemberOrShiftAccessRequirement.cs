using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Requirement satisfied when:
/// - User has the ActiveMember claim, OR
/// - User has TeamsAdmin/Board/Admin roles (admin bypass), OR
/// - User has shift dashboard roles (Admin, NoInfoAdmin, VolunteerCoordinator).
/// Used for Shifts section nav visibility.
/// </summary>
public class ActiveMemberOrShiftAccessRequirement : IAuthorizationRequirement;
