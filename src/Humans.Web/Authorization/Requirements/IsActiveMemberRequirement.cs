using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Requirement satisfied when the user is an active member (has ActiveMember claim)
/// OR has TeamsAdmin/Board/Admin roles.
/// </summary>
public class IsActiveMemberRequirement : IAuthorizationRequirement;
