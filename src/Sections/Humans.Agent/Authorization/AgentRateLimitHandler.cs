using Humans.Agent.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using Humans.Agent.Services;

namespace Humans.Agent.Authorization;

internal sealed class AgentRateLimitHandler(IAgentRateLimitStore rateLimit, IAgentSettingsService settings, IClock clock)
    : AuthorizationHandler<AgentRateLimitRequirement, Guid>
{
    private readonly DateTimeZone _zone = DateTimeZone.Utc;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AgentRateLimitRequirement requirement,
        Guid userId)
    {
        var now = clock.GetCurrentInstant().InZone(_zone);
        var today = now.Date;
        var hour = now.Hour;
        var settings1 = settings.Current;
        var snapshot = rateLimit.Get(userId, today, hour);

        if (snapshot.IsAtLimit(settings1))
        {
            return Task.CompletedTask; // Fail: don't call Succeed.
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
