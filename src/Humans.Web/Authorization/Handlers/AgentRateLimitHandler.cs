using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Web.Authorization.Handlers;

public sealed class AgentRateLimitHandler
    : AuthorizationHandler<AgentRateLimitRequirement, Guid>
{
    private readonly IAgentRateLimitStore _rateLimit;
    private readonly IAgentSettingsService _settings;
    private readonly IClock _clock;
    private readonly DateTimeZone _zone;

    public AgentRateLimitHandler(
        IAgentRateLimitStore rateLimit,
        IAgentSettingsService settings,
        IClock clock)
    {
        _rateLimit = rateLimit;
        _settings = settings;
        _clock = clock;
        _zone = DateTimeZone.Utc;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AgentRateLimitRequirement requirement,
        Guid userId)
    {
        var now = _clock.GetCurrentInstant().InZone(_zone);
        var today = now.Date;
        var settings = _settings.Current;
        var snapshot = _rateLimit.Get(userId, today);

        if (snapshot.MessagesToday >= settings.DailyMessageCap ||
            snapshot.TokensToday >= settings.DailyTokenCap)
        {
            return Task.CompletedTask; // Fail: don't call Succeed.
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
