using AwesomeAssertions;
using Humans.Application.Diagnostics;
using Humans.Application.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Extensions;

public class LoggerTimingExtensionsTests
{
    // ── SelectLogLevel threshold coverage ──────────────────────────────────

    [HumansTheory]
    [InlineData(0, LogLevel.None)]
    [InlineData(999, LogLevel.None)]
    [InlineData(1000, LogLevel.Debug)]
    [InlineData(4999, LogLevel.Debug)]
    [InlineData(5000, LogLevel.Information)]
    [InlineData(14999, LogLevel.Information)]
    [InlineData(15000, LogLevel.Warning)]
    [InlineData(29999, LogLevel.Warning)]
    [InlineData(30000, LogLevel.Error)]
    [InlineData(60000, LogLevel.Error)]
    public void SelectLogLevel_maps_elapsed_to_expected_level(double ms, LogLevel expected)
    {
        LoggerTimingExtensions.SelectLogLevel(ms).Should().Be(expected);
    }

    // ── TimeOperation records into the registry ─────────────────────────────

    [HumansFact]
    public void TimeOperation_records_entry_under_derived_key()
    {
        var logger = NullLogger.Instance;
        var registry = OperationTimingRegistry.Instance;

        // Use the extension with an explicit operation + filePath to get a
        // deterministic key without relying on CallerMemberName.
        using (logger.TimeOperation(operation: "RecordingTestMethod", filePath: "/path/to/TestClass.cs"))
        {
            // body executes here
        }

        var timings = registry.GetTimings();
        timings.Should().Contain(t => t.Key == "TestClass.RecordingTestMethod");
    }

    [HumansFact]
    public void TimeOperation_increments_count_on_repeated_calls()
    {
        var logger = NullLogger.Instance;
        var registry = OperationTimingRegistry.Instance;
        const string op = "RepeatOp";
        const string file = "/src/RepeatClass.cs";

        using (logger.TimeOperation(operation: op, filePath: file)) { }
        using (logger.TimeOperation(operation: op, filePath: file)) { }

        var snapshot = registry.GetTimings()
            .FirstOrDefault(t => string.Equals(t.Key, "RepeatClass.RepeatOp", StringComparison.Ordinal));
        snapshot.Should().NotBeNull();
        snapshot!.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Eat increments swallowed counter ───────────────────────────────────

    [HumansFact]
    public void Eat_increments_swallowed_counter_in_registry()
    {
        var logger = NullLogger.Instance;
        var registry = OperationTimingRegistry.Instance;

        logger.Eat(
            new InvalidOperationException("oops"),
            operation: "EatTestMethod",
            filePath: "/src/EatTestClass.cs");

        var swallowed = registry.GetSwallowed();
        swallowed.Should().Contain(s => s.Key == "EatTestClass.EatTestMethod" && s.Count >= 1);
    }

    // ── OperationTimer disposes correctly ──────────────────────────────────

    [HumansFact]
    public void OperationTimer_double_dispose_is_safe()
    {
        var logger = NullLogger.Instance;
        var timer = logger.TimeOperation(operation: "DoubleDispose", filePath: "/src/SafeClass.cs");
        timer.Dispose();
        var act = timer.Dispose; // second dispose
        act.Should().NotThrow();
    }
}
