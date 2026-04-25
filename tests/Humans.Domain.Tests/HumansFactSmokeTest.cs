using Xunit;

namespace Humans.Domain.Tests;

public class HumansFactSmokeTest
{
    [HumansFact]
    public void Sync_default_timeout_test_runs() => Assert.True(true);

    [HumansFact(Timeout = 5000)]
    public void Sync_explicit_higher_timeout_runs() => Assert.True(true);

    [HumansFact]
    public async Task Async_default_timeout_test_runs()
    {
        await Task.CompletedTask;
        Assert.True(true);
    }

    [HumansTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void Theory_sync_default_timeout_runs(int n) => Assert.True(n > 0);

    [HumansFact]
    public void HumansFact_rejects_zero_timeout()
    {
        var ex = Assert.Throws<ArgumentException>(() => new HumansFactAttribute { Timeout = 0 });
        Assert.Contains("positive timeout", ex.Message, StringComparison.Ordinal);
    }

    [HumansFact]
    public void HumansFact_rejects_negative_timeout()
    {
        Assert.Throws<ArgumentException>(() => new HumansFactAttribute { Timeout = -1 });
    }

    [HumansFact]
    public void HumansTheory_rejects_zero_timeout()
    {
        Assert.Throws<ArgumentException>(() => new HumansTheoryAttribute { Timeout = 0 });
    }
}
