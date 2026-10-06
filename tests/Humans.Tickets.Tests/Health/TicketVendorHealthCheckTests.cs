using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Tickets.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Health;

public class TicketVendorHealthCheckTests
{
    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckHealthAsync_DistinguishesCallerCancellationFromVendorTimeout(bool callerCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<VendorEventSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vendor = Substitute.For<ITicketVendorService>();
        vendor.GetEventSummaryAsync("event", cancellation.Token).Returns(pending.Task);
        var logger = Substitute.For<ILogger<TicketVendorHealthCheck>>();
        var sut = new TicketVendorHealthCheck(vendor, Options.Create(new TicketVendorSettings
        {
            EventId = "event",
            ApiKey = "configured"
        }), logger);

        var probe = sut.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);
        if (callerCanceled) await cancellation.CancelAsync();
        pending.SetCanceled(callerCanceled ? cancellation.Token : CancellationToken.None);

        if (callerCanceled)
        {
            OperationCanceledException? failure = null;
            try
            {
                await probe;
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
            }
            failure.Should().NotBeNull();
            failure!.CancellationToken.Should().Be(cancellation.Token);
            logger.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            (await probe).Status.Should().Be(HealthStatus.Degraded);
        }
    }
}
