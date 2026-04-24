using Xunit;

namespace Humans.Domain.Tests;

/// <summary>
/// Demonstration of xUnit v3's per-test <c>[Fact(Timeout = N)]</c>
/// mechanism. Skipped by default; unskip locally and run to confirm the
/// test runner cancels a cooperative long-running test at the configured
/// millisecond budget (reported as a timeout-caused failure).
///
/// See nobodies-collective/Humans#586 for the motivation (silent 8-minute
/// CI hang on PR peterdrier/Humans#306).
///
/// Note: xUnit v3 timeouts are <em>cooperative</em> — the test gets a
/// cancellation token via <c>TestContext.Current.CancellationToken</c> and
/// is expected to observe it. The CI-level <c>--blame-hang-timeout 2m</c>
/// guard in <c>.github/workflows/build.yml</c> covers the non-cooperative
/// hang case (tight infinite loops that ignore cancellation).
/// </summary>
public class GlobalTimeoutDemoTest
{
    [Fact(
        Skip = "Demo test — unskip locally to verify the 500ms [Fact(Timeout)] enforcement. See nobodies-collective/Humans#586.",
        Timeout = 500)]
    public async Task Cooperative_loop_exceeding_timeout_is_cancelled()
    {
        // Wait cooperatively on the xUnit-provided cancellation token so the
        // runner can cancel us. We request 5s but Timeout = 500ms kicks in
        // first, which cancels this Task.Delay with a TaskCanceledException
        // — xUnit v3 reports that as a timeout-caused test failure.
        var cancellationToken = TestContext.Current.CancellationToken;
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
