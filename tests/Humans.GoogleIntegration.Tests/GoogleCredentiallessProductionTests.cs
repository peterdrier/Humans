using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Health;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleCredentiallessProductionTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Cancelled_activity_reads_stop_before_credential_initialization(bool resolvePerson)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var client = new GoogleDriveActivityClient(Options.Create(new GoogleWorkspaceSettings()),
            NullLogger<GoogleDriveActivityClient>.Instance);
        Func<Task> read = async () =>
        {
            if (resolvePerson)
                await client.TryResolvePersonEmailAsync("people/123", cancelled.Token);
            else
            {
                await foreach (var activity in client.QueryActivityAsync("item", "2026-09-30T00:00:00Z", cancelled.Token))
                    throw new InvalidOperationException("Cancelled query returned an activity.");
            }
        };

        var thrown = await read.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancelled.Token);
    }

    [HumansFact]
    public void Registration_InProductionWithoutCredentials_UsesStubSyncService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [HostDefaults.EnvironmentKey] = Environments.Production
            })
            .Build();
        var services = new ServiceCollection();

        var act = () => new Humans.GoogleIntegration.Section().Register(services, configuration);

        act.Should().NotThrow();
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IGoogleSyncService) &&
            descriptor.ImplementationType == typeof(StubGoogleSyncService));
    }

    [HumansFact]
    public async Task HealthCheck_WithoutCredentials_IsDegraded()
    {
        var check = new GoogleWorkspaceHealthCheck(
            Options.Create(new GoogleWorkspaceSettings()),
            NullLogger<GoogleWorkspaceHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            Xunit.TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Google Workspace not configured");
    }
}
