using AwesomeAssertions;
using Hangfire;
using Humans.Holded.Contracts;
using Humans.Holded.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Holded.Tests;

public sealed class HoldedMirrorCatchUpTests
{
    [HumansTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Boot_QueuesASync_OnlyWhileTheMirrorHasNeverSynced(bool synced, bool queued)
    {
        var holded = Substitute.For<IHoldedService>();
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new HoldedPurchaseDocMirror(
            synced ? Instant.FromUtc(2026, 11, 3, 3, 0) : null, "Idle", null, 0, null, []));
        holded.GetContactsAsync(Arg.Any<CancellationToken>()).Returns(new HoldedContactMirror(
            synced ? Instant.FromUtc(2026, 11, 3, 3, 0) : null, []));
        var jobs = Substitute.For<IBackgroundJobClient>();
        var scopes = new ServiceCollection().AddSingleton(holded).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var catchUp = new HoldedMirrorCatchUp(scopes, jobs,
            Options.Create(new HoldedClientOptions { ApiKey = "key" }), NullLogger<HoldedMirrorCatchUp>.Instance);

        await catchUp.StartAsync(Xunit.TestContext.Current.CancellationToken);

        jobs.ReceivedCalls().Count(c => string.Equals(
                c.GetMethodInfo().Name, nameof(IBackgroundJobClient.Create), StringComparison.Ordinal))
            .Should().Be(queued ? 1 : 0);
    }
}
