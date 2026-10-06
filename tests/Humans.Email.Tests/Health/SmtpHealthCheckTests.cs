using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests.Health;

public class SmtpHealthCheckTests
{
    [HumansFact]
    public async Task CheckHealthAsync_ConfiguredProbePreservesCancellationWithoutLoggingFailure()
    {
        var logger = Substitute.For<ILogger<SmtpHealthCheck>>();
        var sut = new SmtpHealthCheck(Options.Create(new EmailSettings()), logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        var failure = await act.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        logger.ReceivedCalls().Should().BeEmpty();
    }
}
