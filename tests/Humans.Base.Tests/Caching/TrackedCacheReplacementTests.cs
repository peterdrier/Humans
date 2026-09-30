using AwesomeAssertions;
using Humans.Base.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Base.Tests.Caching;

public class TrackedCacheReplacementTests
{
    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task FailedReplacement_DiscardsStaleValueAndRetriesSource(bool warmOnStartup, bool previouslyCached)
    {
        var sut = new ReplacementCache(warmOnStartup);
        await sut.WarmAsync(TestContext.Current.CancellationToken);
        if (previouslyCached) sut.Set(1, "old");
        sut.SourceValue = "committed";
        sut.LoadFailure = new InvalidOperationException("reload unavailable");

        var replace = () => sut.ReplaceAsync(1, TestContext.Current.CancellationToken);
        await replace.Should().ThrowAsync<InvalidOperationException>().WithMessage("reload unavailable");

        sut.ContainsKey(1).Should().BeFalse();
        if (warmOnStartup) sut.IsWarmedUp.Should().BeFalse();
        sut.LoadFailure = null;
        if (warmOnStartup)
        {
            await sut.WarmAsync(TestContext.Current.CancellationToken);
            sut.Values.Should().ContainSingle().Which.Should().Be("committed");
        }
        else
        {
            (await sut.GetAsync(1, TestContext.Current.CancellationToken)).Should().Be("committed");
        }
    }

    [HumansFact]
    public async Task CancelledReplacement_InvalidatesStaleValueAndPreservesCancellation()
    {
        var sut = new ReplacementCache(warmOnStartup: true);
        await sut.WarmAsync(TestContext.Current.CancellationToken);
        sut.Set(1, "old");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var replace = () => sut.ReplaceAsync(1, cancellation.Token);
        var failure = await replace.Should().ThrowAsync<OperationCanceledException>();

        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        sut.ContainsKey(1).Should().BeFalse();
        sut.IsWarmedUp.Should().BeFalse();
    }

    private sealed class ReplacementCache(bool warmOnStartup)
        : TrackedCache<int, string>("replacement-test", warmOnStartup, NullLogger.Instance)
    {
        public string? SourceValue { get; set; }
        public Exception? LoadFailure { get; set; }

        public Task WarmAsync(CancellationToken ct) => EnsureWarmedAsync(ct);

        protected override Task WarmAllAsync(CancellationToken ct)
        {
            if (SourceValue is not null) Set(1, SourceValue);
            return Task.CompletedTask;
        }

        protected override ValueTask<string?> LoadRowAsync(int key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (LoadFailure is not null) throw LoadFailure;
            return ValueTask.FromResult(SourceValue);
        }
    }
}
