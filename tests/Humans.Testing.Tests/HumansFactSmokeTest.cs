using Xunit;

namespace Humans.Testing.Tests;

public class HumansFactSmokeTest
{
    [HumansFact]
    public void SyncTest_DefaultTimeout_Runs() => Assert.True(true);

    [HumansFact(Timeout = 5000)]
    public void SyncTest_HigherTimeout_Runs() =>
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

    [HumansFact]
    public async Task AsyncTest_DefaultTimeout_Runs()
    {
        await Task.CompletedTask;
        Assert.True(true);
    }

    [HumansTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void SyncTheory_DefaultTimeout_Runs(int n) => Assert.True(n > 0);

    [HumansFact]
    public void DefaultTimeouts_AreThirtySeconds()
    {
        Assert.Equal(30000, new HumansFactAttribute().Timeout);
        Assert.Equal(30000, new HumansTheoryAttribute().Timeout);
    }

    [HumansFact]
    public void HumansFact_ZeroTimeout_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new HumansFactAttribute { Timeout = 0 });
        Assert.Contains("positive timeout", ex.Message, StringComparison.Ordinal);
    }

    [HumansFact]
    public void HumansFact_NegativeTimeout_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new HumansFactAttribute { Timeout = -1 });
    }

    [HumansFact]
    public void HumansTheory_ZeroTimeout_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new HumansTheoryAttribute { Timeout = 0 });
    }
}
