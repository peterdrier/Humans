using AwesomeAssertions;
using Humans.Agent.Services;
using Humans.Agent.Domain;
using NodaTime;
using Humans.Agent.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Humans.Agent.Tests.Health;

public class AnthropicHealthCheckTests
{
    [HumansFact]
    public async Task CheckHealthAsync_EnabledProbePreservesCallerCancellation()
    {
        var availability = Substitute.For<IAgentSettingsService>();
        availability.Current.Returns(new AgentSettingsDto(
            Enabled: true, Model: "m", PreloadConfig: AgentPreloadConfig.Tier1,
            DailyMessageCap: 10, HourlyMessageCap: 5, DailyTokenCap: 50_000,
            RetentionDays: 30, UpdatedAt: Instant.MinValue));
        var sut = new AnthropicHealthCheck(availability);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        var failure = await act.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
    }
}
