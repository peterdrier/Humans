using AwesomeAssertions;
using Humans.Web.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Tests.Infrastructure;

public class RateLimitRejectionAggregatorTests
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMilliseconds(100);

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    private static async Task<IReadOnlyList<string>> WaitForFlush(ListLogger logger)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        return logger.Messages;
    }

    // ── only the first rejection in a window asks for the detailed log ───────

    [HumansFact]
    public void FirstRejectionInWindowIsDetailed_SubsequentAreNot()
    {
        var aggregator = new RateLimitRejectionAggregator(new ListLogger(), TimeSpan.FromMinutes(5));

        aggregator.RecordRejection("1.2.3.4").Should().BeTrue("first rejection must log in full detail");
        aggregator.RecordRejection("1.2.3.4").Should().BeFalse();
        aggregator.RecordRejection("1.2.3.4").Should().BeFalse();
        aggregator.RecordRejection("5.6.7.8").Should().BeTrue("each source IP gets its own window");
    }

    // ── a burst flushes exactly one summary carrying the count ───────────────

    [HumansFact]
    public async Task BurstFlushesOneSummaryWithCount()
    {
        var logger = new ListLogger();
        var aggregator = new RateLimitRejectionAggregator(logger, ShortWindow);

        for (var i = 0; i < 5; i++)
            aggregator.RecordRejection("1.2.3.4");

        var messages = await WaitForFlush(logger);
        messages.Should().ContainSingle()
            .Which.Should().Contain("1.2.3.4").And.Contain("5 times");
    }

    // ── a lone rejection was already logged in detail; no summary ────────────

    [HumansFact]
    public async Task SingleRejectionProducesNoSummary()
    {
        var logger = new ListLogger();
        var aggregator = new RateLimitRejectionAggregator(logger, ShortWindow);

        aggregator.RecordRejection("1.2.3.4");

        await Task.Delay(ShortWindow * 5);
        logger.Messages.Should().BeEmpty("a count of 1 is already fully covered by the detailed line");
    }

    // ── after the window flushes, the next rejection is detailed again ───────

    [HumansFact]
    public async Task NewWindowAfterFlushIsDetailedAgain()
    {
        var logger = new ListLogger();
        var aggregator = new RateLimitRejectionAggregator(logger, ShortWindow);

        aggregator.RecordRejection("1.2.3.4");
        aggregator.RecordRejection("1.2.3.4");

        await WaitForFlush(logger);
        aggregator.RecordRejection("1.2.3.4").Should().BeTrue("the flushed window must not suppress future detail");
    }
}
