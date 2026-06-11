using AwesomeAssertions;
using Humans.Application.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Threading;

public class TrackedLockTests
{
    // =========================================================================
    // Uncontended acquire / release
    // =========================================================================

    [HumansFact]
    public async Task AcquireAsync_uncontended_acquires_and_releases()
    {
        var sut = new TrackedLock("test");

        using (var releaser = await sut.AcquireAsync(NullLogger.Instance))
        {
            // Inside the lock — no exception means it was acquired.
        }

        // After dispose the lock must be available again.
        var act = async () =>
        {
            using var r = await sut.AcquireAsync(NullLogger.Instance,
                CancellationToken.None);
        };
        await act.Should().NotThrowAsync("lock should be available again after the first release");
    }

    // =========================================================================
    // Releaser is single-release (double-dispose safe)
    // =========================================================================

    [HumansFact]
    public async Task Releaser_double_dispose_is_safe()
    {
        var sut = new TrackedLock("test");

        var releaser = await sut.AcquireAsync(NullLogger.Instance);
        releaser.Dispose();

        var act = () => { releaser.Dispose(); };
        act.Should().NotThrow("second dispose must be silently ignored");

        // Lock should be acquirable exactly once after a single release.
        using var r2 = await sut.AcquireAsync(NullLogger.Instance);
    }

    // =========================================================================
    // Mutual exclusion: two concurrent acquirers serialize
    // =========================================================================

    [HumansFact]
    public async Task AcquireAsync_serializes_concurrent_acquirers()
    {
        var sut = new TrackedLock("test");
        var insideConcurrently = false;
        var violation = false;

        var first = await sut.AcquireAsync(NullLogger.Instance);

        var secondTask = Task.Run(async () =>
        {
            using var r = await sut.AcquireAsync(NullLogger.Instance);
            if (insideConcurrently) violation = true;
        });

        // Give the second task a moment to reach WaitAsync.
        await Task.Delay(20);
        insideConcurrently = true;
        first.Dispose();
        insideConcurrently = false;

        await secondTask;
        violation.Should().BeFalse("the two acquirers must not be inside the lock simultaneously");
    }

    // =========================================================================
    // WaitToLogLevel pure-function thresholds
    // =========================================================================

    [HumansTheory]
    [InlineData(0, null)]          // below debug → no log
    [InlineData(500, null)]        // 500ms < 1s debug threshold → no log
    [InlineData(1000, LogLevel.Debug)]
    [InlineData(4999, LogLevel.Debug)]
    [InlineData(5000, LogLevel.Information)]
    [InlineData(14999, LogLevel.Information)]
    [InlineData(15000, LogLevel.Warning)]
    [InlineData(60000, LogLevel.Warning)]
    public void WaitToLogLevel_returns_expected_level(int elapsedMs, LogLevel? expected)
    {
        var debug = TimeSpan.FromSeconds(1);
        var info = TimeSpan.FromSeconds(5);
        var warn = TimeSpan.FromSeconds(15);

        var result = TrackedLock.WaitToLogLevel(
            TimeSpan.FromMilliseconds(elapsedMs), debug, info, warn);

        result.Should().Be(expected);
    }

    // =========================================================================
    // Contended wait logs via the injected logger
    // =========================================================================

    [HumansFact]
    public async Task AcquireAsync_contended_logs_when_wait_exceeds_debug_threshold()
    {
        // Tiny thresholds so the test doesn't sleep for real seconds.
        var sut = new TrackedLock("contended-test",
            timeout: TimeSpan.FromSeconds(5),
            debugThreshold: TimeSpan.FromMilliseconds(1),
            informationThreshold: TimeSpan.FromSeconds(60),
            warningThreshold: TimeSpan.FromSeconds(120));

        var logger = Substitute.For<ILogger>();

        // Acquire first to force contention.
        var first = await sut.AcquireAsync(NullLogger.Instance);

        var secondTask = Task.Run(async () =>
        {
            // Small delay ensures the waiter spends > 1ms waiting.
            using var r = await sut.AcquireAsync(logger);
        });

        await Task.Delay(10);
        first.Dispose();
        await secondTask;

        logger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception?>(e => e == null),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // =========================================================================
    // Timeout: throws TimeoutException with lock name in message
    // =========================================================================

    [HumansFact]
    public async Task AcquireAsync_throws_TimeoutException_when_lock_held_past_timeout()
    {
        const string lockName = "timeout-test-lock";
        var sut = new TrackedLock(lockName,
            timeout: TimeSpan.FromMilliseconds(30));

        var logger = NullLogger.Instance;

        // Hold the lock indefinitely.
        var holder = await sut.AcquireAsync(logger);

        var act = async () => await sut.AcquireAsync(logger);

        var ex = await act.Should().ThrowAsync<TimeoutException>();
        ex.WithMessage($"*{lockName}*");

        holder.Dispose();
    }

    // =========================================================================
    // Cancellation: waiting acquire honours a cancelled token
    // =========================================================================

    [HumansFact]
    public async Task AcquireAsync_cancelled_token_throws_OperationCanceledException()
    {
        var sut = new TrackedLock("cancel-test",
            timeout: TimeSpan.FromSeconds(10));

        // Hold the lock so the second acquire has to wait.
        var holder = await sut.AcquireAsync(NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () =>
        {
            using var r = await sut.AcquireAsync(NullLogger.Instance, cts.Token);
        };
        await act.Should().ThrowAsync<OperationCanceledException>();

        holder.Dispose();
    }
}
