using AwesomeAssertions;
using Humans.Application.Diagnostics;

namespace Humans.Application.Tests.Diagnostics;

public class OperationTimingRegistryTests
{
    [HumansFact]
    public void Single_record_produces_correct_aggregates()
    {
        var reg = new OperationTimingRegistry();
        reg.Record("Foo.Bar", 200.0);

        var snapshots = reg.GetTimings();
        snapshots.Should().HaveCount(1);

        var s = snapshots[0];
        s.Key.Should().Be("Foo.Bar");
        s.Count.Should().Be(1);
        s.TotalMs.Should().Be(200.0);
        s.MinMs.Should().Be(200.0);
        s.MaxMs.Should().Be(200.0);
        s.LastMs.Should().Be(200.0);
        s.AvgMs.Should().Be(200.0);
    }

    [HumansFact]
    public void Multiple_records_accumulate_min_max_total_correctly()
    {
        var reg = new OperationTimingRegistry();
        reg.Record("Op.Method", 100.0);
        reg.Record("Op.Method", 300.0);
        reg.Record("Op.Method", 200.0);

        var s = reg.GetTimings()[0];
        s.Count.Should().Be(3);
        s.TotalMs.Should().Be(600.0);
        s.MinMs.Should().Be(100.0);
        s.MaxMs.Should().Be(300.0);
        s.LastMs.Should().Be(200.0);
        s.AvgMs.Should().Be(200.0);
    }

    [HumansFact]
    public void Records_for_different_keys_are_isolated()
    {
        var reg = new OperationTimingRegistry();
        reg.Record("A.X", 50.0);
        reg.Record("B.Y", 150.0);

        var timings = reg.GetTimings().ToDictionary(t => t.Key, StringComparer.Ordinal);
        timings["A.X"].TotalMs.Should().Be(50.0);
        timings["B.Y"].TotalMs.Should().Be(150.0);
    }

    [HumansFact]
    public void Snapshot_is_immutable_from_subsequent_records()
    {
        var reg = new OperationTimingRegistry();
        reg.Record("Key", 10.0);
        var before = reg.GetTimings();

        reg.Record("Key", 20.0);
        var after = reg.GetTimings();

        before[0].Count.Should().Be(1);
        after[0].Count.Should().Be(2);
    }

    [HumansFact]
    public void Swallowed_exception_counter_increments_per_key()
    {
        var reg = new OperationTimingRegistry();
        reg.IncrementSwallowed("A.Method");
        reg.IncrementSwallowed("A.Method");
        reg.IncrementSwallowed("B.Other");

        var swallowed = reg.GetSwallowed().ToDictionary(s => s.Key, StringComparer.Ordinal);
        swallowed["A.Method"].Count.Should().Be(2);
        swallowed["B.Other"].Count.Should().Be(1);
    }

    [HumansFact]
    public void Concurrent_records_produce_consistent_count()
    {
        var reg = new OperationTimingRegistry();
        const int iterations = 1000;

        Parallel.For(0, iterations, i => reg.Record("Concurrent.Op", i));

        var s = reg.GetTimings()[0];
        s.Count.Should().Be(iterations);
    }

    [HumansFact]
    public void Avg_ms_is_zero_on_empty_registry()
    {
        var snapshot = new OperationTimingSnapshot("K", 0, 0, 0, 0, 0, default);
        snapshot.AvgMs.Should().Be(0);
    }
}
