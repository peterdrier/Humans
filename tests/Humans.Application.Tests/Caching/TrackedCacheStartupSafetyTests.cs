using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.Caching;

public class TrackedCacheStartupSafetyTests
{
    [HumansFact]
    public async Task StartAsync_swallows_warmup_exception_and_logs_warning()
    {
        var logger = Substitute.For<ILogger>();
        var sut = new ThrowingCache("test-cache", warmOnStartup: true, logger);

        var act = async () => await ((IHostedService)sut).StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "no-startup-guards HARD RULE — the host MUST boot even when warmup fails");

        sut.WarmAttempts.Should().Be(1);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception?>(e => e == null),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task StartAsync_completes_then_next_read_retries_warm()
    {
        var sut = new ThrowingCache("test-cache", warmOnStartup: true);

        await ((IHostedService)sut).StartAsync(CancellationToken.None);
        sut.WarmAttempts.Should().Be(1, "first attempt failed during startup");

        sut.ShouldThrow = false;
        await sut.WarmFromOutsideAsync(CancellationToken.None);

        sut.WarmAttempts.Should().Be(2, "subsequent read must re-trigger warm");
        sut.IsWarmedUpExposed.Should().BeTrue("recovery warm succeeded");
    }

    [HumansFact]
    public async Task StartAsync_propagates_OperationCanceledException_when_token_canceled()
    {
        using var cts = new CancellationTokenSource();
        var sut = new CancellingCache("test-cache", warmOnStartup: true, cts);

        var act = async () => await ((IHostedService)sut).StartAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "shutdown during startup must propagate, not be swallowed");
    }

    [HumansFact]
    public async Task StartAsync_is_noop_when_warmOnStartup_false()
    {
        var sut = new ThrowingCache("test-cache", warmOnStartup: false);

        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        sut.WarmAttempts.Should().Be(0);
    }

    private sealed class ThrowingCache(string name, bool warmOnStartup, ILogger? logger = null)
        : TrackedCache<Guid, string>(name, warmOnStartup, logger ?? NullLogger.Instance)
    {
        public int WarmAttempts;
        public bool ShouldThrow = true;
        public bool IsWarmedUpExposed => IsWarmedUp;

        public Task WarmFromOutsideAsync(CancellationToken ct) => EnsureWarmedAsync(ct);

        protected override Task WarmAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref WarmAttempts);
            if (ShouldThrow)
                throw new InvalidOperationException("simulated DB unavailable");
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingCache(string name, bool warmOnStartup, CancellationTokenSource cts)
        : TrackedCache<Guid, string>(name, warmOnStartup, NullLogger.Instance)
    {
        protected override async Task WarmAllAsync(CancellationToken ct)
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
    }
}
