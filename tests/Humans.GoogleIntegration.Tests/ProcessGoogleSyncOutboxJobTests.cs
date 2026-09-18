using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

public sealed class ProcessGoogleSyncOutboxJobTests
{
    [HumansFact]
    public async Task ExecuteAsync_WithoutCredentials_ReportsSkipped()
    {
        var outbox = Substitute.For<IGoogleSyncOutboxProcessor>();
        var googleClient = Substitute.For<IGoogleDriveActivityClient>();
        googleClient.IsConfigured.Returns(false);
        var metrics = Substitute.For<IHumansMetrics>();
        var job = new ProcessGoogleSyncOutboxJob(
            outbox,
            googleClient,
            metrics,
            NullLogger<ProcessGoogleSyncOutboxJob>.Instance);

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        metrics.Received(1).RecordJobRun("process_google_sync_outbox", "skipped");
        metrics.DidNotReceive().RecordJobRun("process_google_sync_outbox", "success");
    }
}
