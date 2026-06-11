using AwesomeAssertions;
using Humans.Web.Infrastructure;
using Serilog.Events;
using Serilog.Parsing;

namespace Humans.Web.Tests.Infrastructure;

public class InMemoryLogSinkTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static LogEvent MakeEvent(LogEventLevel level, DateTimeOffset? timestamp = null) => new(
        timestamp: timestamp ?? DateTimeOffset.UtcNow,
        level: level,
        exception: null,
        messageTemplate: new MessageTemplate([]),
        properties: []);

    // ── warning buffer cycles; earlier errors are still present ──────────────

    [HumansFact]
    public void WarningsCannotEvictErrors()
    {
        // Warning cap = 3, error cap = 10.  Flood warnings past their cap while
        // keeping one error emitted before the flood.  The error must survive.
        var sink = new InMemoryLogSink(warningCapacity: 3, errorCapacity: 10);

        var error = MakeEvent(LogEventLevel.Error);
        sink.Emit(error);

        for (var i = 0; i < 5; i++)
            sink.Emit(MakeEvent(LogEventLevel.Warning));

        var all = sink.GetEvents(count: 1000);
        all.Should().Contain(error, "error must not be evicted by warning overflow");
    }

    // ── error buffer respects its own cap ────────────────────────────────────

    [HumansFact]
    public void ErrorBufferCapsAtErrorCapacity()
    {
        var sink = new InMemoryLogSink(warningCapacity: 100, errorCapacity: 3);

        for (var i = 0; i < 5; i++)
            sink.Emit(MakeEvent(LogEventLevel.Error));

        var events = sink.GetEvents(count: 1000, minLevel: LogEventLevel.Error);
        events.Should().HaveCount(3, "error buffer must drop oldest when full");
    }

    [HumansFact]
    public void FatalEventsUseErrorBuffer()
    {
        var sink = new InMemoryLogSink(warningCapacity: 100, errorCapacity: 2);

        for (var i = 0; i < 4; i++)
            sink.Emit(MakeEvent(LogEventLevel.Fatal));

        var events = sink.GetEvents(count: 1000, minLevel: LogEventLevel.Error);
        events.Should().HaveCount(2);
    }

    // ── merged GetEvents ordering (newest first) ─────────────────────────────

    [HumansFact]
    public void GetEvents_ReturnsNewestFirst()
    {
        var sink = new InMemoryLogSink(warningCapacity: 10, errorCapacity: 10);
        var t0 = DateTimeOffset.UtcNow;

        sink.Emit(MakeEvent(LogEventLevel.Warning, t0.AddSeconds(1)));
        sink.Emit(MakeEvent(LogEventLevel.Error, t0.AddSeconds(3)));
        sink.Emit(MakeEvent(LogEventLevel.Warning, t0.AddSeconds(2)));

        var events = sink.GetEvents(count: 1000);

        events.Should().HaveCount(3);
        events[0].Timestamp.Should().Be(t0.AddSeconds(3));
        events[1].Timestamp.Should().Be(t0.AddSeconds(2));
        events[2].Timestamp.Should().Be(t0.AddSeconds(1));
    }

    // ── minimum-level filter ─────────────────────────────────────────────────

    [HumansFact]
    public void GetEvents_MinLevelError_ExcludesWarnings()
    {
        var sink = new InMemoryLogSink(warningCapacity: 10, errorCapacity: 10);

        sink.Emit(MakeEvent(LogEventLevel.Warning));
        sink.Emit(MakeEvent(LogEventLevel.Error));
        sink.Emit(MakeEvent(LogEventLevel.Fatal));

        var events = sink.GetEvents(count: 1000, minLevel: LogEventLevel.Error);

        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Level >= LogEventLevel.Error);
    }

    [HumansFact]
    public void GetEvents_NoMinLevel_ReturnsAllBufferedLevels()
    {
        var sink = new InMemoryLogSink(warningCapacity: 10, errorCapacity: 10);

        sink.Emit(MakeEvent(LogEventLevel.Warning));
        sink.Emit(MakeEvent(LogEventLevel.Error));

        sink.GetEvents(count: 1000).Should().HaveCount(2);
    }

    [HumansFact]
    public void GetEvents_CountLimitsResults()
    {
        var sink = new InMemoryLogSink(warningCapacity: 10, errorCapacity: 10);

        for (var i = 0; i < 5; i++)
            sink.Emit(MakeEvent(LogEventLevel.Warning));
        for (var i = 0; i < 5; i++)
            sink.Emit(MakeEvent(LogEventLevel.Error));

        sink.GetEvents(count: 3).Should().HaveCount(3);
    }

    // ── lifetime counts unaffected by eviction ───────────────────────────────

    [HumansFact]
    public void LifetimeCounts_SurviveWarningEviction()
    {
        var sink = new InMemoryLogSink(warningCapacity: 2, errorCapacity: 10);

        for (var i = 0; i < 5; i++)
            sink.Emit(MakeEvent(LogEventLevel.Warning));

        var counts = sink.GetLifetimeCounts();
        counts[LogEventLevel.Warning].Should().Be(5, "lifetime count must not drop when buffer cycles");
    }

    [HumansFact]
    public void LifetimeCounts_SurviveErrorEviction()
    {
        var sink = new InMemoryLogSink(warningCapacity: 10, errorCapacity: 2);

        for (var i = 0; i < 4; i++)
            sink.Emit(MakeEvent(LogEventLevel.Error));

        var counts = sink.GetLifetimeCounts();
        counts[LogEventLevel.Error].Should().Be(4);
    }

    [HumansFact]
    public void TotalEvents_SumsAllLevels()
    {
        var sink = new InMemoryLogSink(warningCapacity: 1, errorCapacity: 1);

        sink.Emit(MakeEvent(LogEventLevel.Warning));
        sink.Emit(MakeEvent(LogEventLevel.Warning)); // evicts the first
        sink.Emit(MakeEvent(LogEventLevel.Error));

        sink.TotalEvents.Should().Be(3);
    }
}
