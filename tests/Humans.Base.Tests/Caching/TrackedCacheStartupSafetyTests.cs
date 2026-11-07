using AwesomeAssertions;
using Humans.Base.Caching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Base.Tests.Caching;

public class TrackedCacheStartupSafetyTests
{
    [HumansFact]
    public async Task StartAsync_swallows_warmup_exception_and_logs_warning()
    {
        var logger = Substitute.For<ILogger>();
        var sut = new ThrowingCache("test-cache", warmOnStartup: true, logger);

        var act = async () => await ((IHostedService)sut).StartAsync(Xunit.TestContext.Current.CancellationToken);

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

        await ((IHostedService)sut).StartAsync(Xunit.TestContext.Current.CancellationToken);
        sut.WarmAttempts.Should().Be(1, "first attempt failed during startup");

        sut.ShouldThrow = false;
        await sut.WarmFromOutsideAsync(Xunit.TestContext.Current.CancellationToken);

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

        await ((IHostedService)sut).StartAsync(Xunit.TestContext.Current.CancellationToken);

        sut.WarmAttempts.Should().Be(0);
    }

    [HumansTheory]
    [Xunit.InlineData("Refresh", false)]
    [Xunit.InlineData("Refresh", true)]
    [Xunit.InlineData("Delete", false)]
    [Xunit.InlineData("Delete", true)]
    [Xunit.InlineData("Invalidate", false)]
    [Xunit.InlineData("Invalidate", true)]
    [Xunit.InlineData("Clear", false)]
    [Xunit.InlineData("Clear", true)]
    public async Task Warmup_OverlappingMutation_ReloadsACompleteCurrentSnapshot(string mutation, bool afterPublication)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var sut = new PausedWarmCache(afterPublication);
        var warm = sut.WarmAsync(ct);
        await sut.Paused.Task.WaitAsync(ct);
        sut.Source[1] = "fresh";
        switch (mutation)
        {
            case "Refresh": sut.Replace(1, "fresh"); break;
            case "Delete": sut.Source.Remove(1); sut.DeleteKey(1); break;
            case "Invalidate": sut.Invalidate(1); break;
            case "Clear": sut.Clear(); break;
        }
        sut.Resume.SetResult();

        await warm;

        sut.AsReadOnlyDictionary.Should().BeEquivalentTo(sut.Source);
        sut.IsWarmedUp.Should().BeTrue();
        sut.WarmAttempts.Should().Be(2);
    }

    private sealed class PausedWarmCache(bool afterPublication)
        : TrackedCache<int, string>("paused-warm", true, NullLogger.Instance)
    {
        public Dictionary<int, string> Source { get; } = new() { [1] = "old", [2] = "unchanged" };
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WarmAttempts { get; private set; }
        public Task WarmAsync(CancellationToken ct) => EnsureWarmedAsync(ct);

        protected override async Task WarmAllAsync(Action<int, string> publish, CancellationToken ct)
        {
            var rows = Source.ToArray();
            var first = ++WarmAttempts == 1;
            if (first && !afterPublication) await PauseAsync(ct);
            foreach (var (key, value) in rows) publish(key, value);
            if (first && afterPublication) await PauseAsync(ct);
        }

        private async Task PauseAsync(CancellationToken ct)
        {
            Paused.SetResult();
            await Resume.Task.WaitAsync(ct);
        }
    }

    private sealed class ThrowingCache(string name, bool warmOnStartup, ILogger? logger = null)
        : TrackedCache<Guid, string>(name, warmOnStartup, logger ?? NullLogger.Instance)
    {
        public int WarmAttempts;
        public bool ShouldThrow = true;
        public bool IsWarmedUpExposed => IsWarmedUp;

        public Task WarmFromOutsideAsync(CancellationToken ct) => EnsureWarmedAsync(ct);

        protected override Task WarmAllAsync(Action<Guid, string> publish, CancellationToken ct)
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
        protected override async Task WarmAllAsync(Action<Guid, string> publish, CancellationToken ct)
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
    }
}
