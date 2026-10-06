using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Agent.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Humans.Agent.Tests.Health;

public class AnthropicHealthCheckTests
{
    [HumansFact]
    public async Task CheckHealthAsync_EnabledProbePreservesCallerCancellation()
    {
        var availability = Substitute.For<IAgentAvailability>();
        availability.IsEnabled.Returns(true);
        var sut = new AnthropicHealthCheck(availability);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        var failure = await act.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
    }
}
