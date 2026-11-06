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
    public enum CatchUpFailure { MirrorRead, Enqueue }

    [HumansTheory]
    [InlineData(CatchUpFailure.MirrorRead)]
    [InlineData(CatchUpFailure.Enqueue)]
    public async Task Boot_OptionalCatchUpFailure_DoesNotAbortStartup(CatchUpFailure stage)
    {
        var failure = new IOException("Catch-up dependency unavailable");
        var holded = Substitute.For<IHoldedService>();
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(
            new HoldedPurchaseDocMirror(null, "Idle", null, 0, null, []));
        var jobs = Substitute.For<IBackgroundJobClient>();
        if (stage == CatchUpFailure.MirrorRead)
            holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException<HoldedPurchaseDocMirror>(failure));
        else
            jobs.When(client => client.Create(Arg.Any<Hangfire.Common.Job>(), Arg.Any<Hangfire.States.IState>()))
                .Do(_ => throw failure);
        using var services = new ServiceCollection().AddSingleton(holded).BuildServiceProvider();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<HoldedMirrorCatchUp>>();
        var catchUp = new HoldedMirrorCatchUp(services.GetRequiredService<IServiceScopeFactory>(), jobs,
            Options.Create(new HoldedClientOptions { ApiKey = "key" }), logger);

        await catchUp.StartAsync(Xunit.TestContext.Current.CancellationToken);

        logger.ReceivedCalls().Select(call => call.GetArguments()).Should().Contain(args =>
            (Microsoft.Extensions.Logging.LogLevel)args[0]! == Microsoft.Extensions.Logging.LogLevel.Warning && ReferenceEquals(args[3], failure));
    }

    [HumansFact]
    public async Task Boot_RequestedCancellation_StillPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var holded = Substitute.For<IHoldedService>();
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<HoldedPurchaseDocMirror>(cancellation.Token));
        using var services = new ServiceCollection().AddSingleton(holded).BuildServiceProvider();
        var catchUp = new HoldedMirrorCatchUp(services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IBackgroundJobClient>(), Options.Create(new HoldedClientOptions { ApiKey = "key" }),
            NullLogger<HoldedMirrorCatchUp>.Instance);

        var start = () => catchUp.StartAsync(cancellation.Token);

        (await start.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [HumansTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Boot_QueuesASync_OnlyWhileTheMirrorHasNeverSynced(bool synced, bool queued)
    {
        var holded = Substitute.For<IHoldedService>();
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new HoldedPurchaseDocMirror(
            synced ? Instant.FromUtc(2026, 11, 3, 3, 0) : null, "Idle", null, 0, null, []));
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
