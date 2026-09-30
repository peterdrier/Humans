using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class StubWorkspaceUserDirectoryClientTests
{
    [HumansFact]
    public async Task ProvisionAccountAsync_UsesTheInjectedClockForCreationTime()
    {
        var now = Instant.FromUtc(2026, 9, 29, 6, 10);
        var sut = new StubWorkspaceUserDirectoryClient(
            NullLogger<StubWorkspaceUserDirectoryClient>.Instance,
            new FakeClock(now));

        var account = await sut.ProvisionAccountAsync(
            "new@nobodies.team", "New", "Person", "unused", null,
            Xunit.TestContext.Current.CancellationToken);

        account.CreationTime.Should().Be(now.ToDateTimeUtc());
    }
}
