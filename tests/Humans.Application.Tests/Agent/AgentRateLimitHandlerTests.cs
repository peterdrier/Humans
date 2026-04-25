using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Infrastructure.Stores;
using Humans.Web.Authorization.Handlers;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentRateLimitHandlerTests
{
    [HumansFact]
    public async Task Allows_when_under_daily_cap()
    {
        var user = Guid.NewGuid();
        var store = new AgentRateLimitStore();
        var settings = FakeSettings(new AgentSettings { DailyMessageCap = 30, DailyTokenCap = 50_000 });
        var handler = new AgentRateLimitHandler(store, settings, FakeClock(2026, 4, 21));

        var context = new AuthorizationHandlerContext(
            [new AgentRateLimitRequirement()],
            new System.Security.Claims.ClaimsPrincipal(),
            user);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Rejects_when_daily_messages_cap_hit()
    {
        var user = Guid.NewGuid();
        var store = new AgentRateLimitStore();
        store.Record(user, new LocalDate(2026, 4, 21), messagesDelta: 30, tokensDelta: 0);
        var settings = FakeSettings(new AgentSettings { DailyMessageCap = 30, DailyTokenCap = 50_000 });
        var handler = new AgentRateLimitHandler(store, settings, FakeClock(2026, 4, 21));

        var context = new AuthorizationHandlerContext(
            [new AgentRateLimitRequirement()],
            new System.Security.Claims.ClaimsPrincipal(),
            user);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static IAgentSettingsService FakeSettings(AgentSettings s)
    {
        var svc = Substitute.For<IAgentSettingsService>();
        svc.Current.Returns(s);
        return svc;
    }

    private static IClock FakeClock(int y, int m, int d) =>
        new FakeClockImpl(Instant.FromUtc(y, m, d, 12, 0));

    private sealed class FakeClockImpl(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
