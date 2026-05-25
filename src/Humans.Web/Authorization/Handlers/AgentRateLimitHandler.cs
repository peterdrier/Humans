using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Web.Authorization.Handlers;

public sealed class AgentRateLimitHandler(IAgentRateLimitStore rateLimit, IAgentSettingsService settings, IClock clock)
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

        if (snapshot.MessagesToday >= settings1.DailyMessageCap ||
            snapshot.TokensToday >= settings1.DailyTokenCap ||
            snapshot.MessagesThisHour >= settings1.HourlyMessageCap)
        {
            return Task.CompletedTask; // Fail: don't call Succeed.
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
