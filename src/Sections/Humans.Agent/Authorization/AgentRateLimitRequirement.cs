using Microsoft.AspNetCore.Authorization;

namespace Humans.Agent.Authorization;

internal sealed class AgentRateLimitRequirement : IAuthorizationRequirement;
