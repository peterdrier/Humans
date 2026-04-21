using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

public sealed class AgentRateLimitRequirement : IAuthorizationRequirement;
