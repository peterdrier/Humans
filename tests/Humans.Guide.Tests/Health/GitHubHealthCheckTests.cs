using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Guide.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Guide.Tests.Health;

public class GitHubHealthCheckTests
{
    [HumansFact]
    public async Task CheckHealthAsync_CallerCancellationPropagatesWithoutLoggingAnAvailabilityFailure()
    {
        var logger = Substitute.For<ILogger<GitHubHealthCheck>>();
        var sut = new GitHubHealthCheck(Options.Create(new GitHubSettings()), logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        var failure = await act.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        logger.ReceivedCalls().Should().BeEmpty();
    }
}
