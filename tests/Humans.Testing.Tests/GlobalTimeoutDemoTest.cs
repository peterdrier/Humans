using Xunit;

namespace Humans.Testing.Tests;

/// <summary>
/// Debugger-only demo of xUnit v3's <c>[Fact(Timeout = N)]</c>. Timeouts are cooperative; the CI
/// <c>--blame-hang-timeout</c> covers loops that ignore cancellation.
/// </summary>
public class GlobalTimeoutDemoTest
{
    [DebuggerOnlyFact(Timeout = 500)]
    public async Task CooperativeLoop_ExceedsTimeout_IsCancelled()
    {
        // Timeout fires before the 5s delay, cancelling it; xUnit reports that as a timeout failure.
        var cancellationToken = TestContext.Current.CancellationToken;
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
