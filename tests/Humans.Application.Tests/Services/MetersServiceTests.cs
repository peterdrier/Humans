using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Humans.Infrastructure.Services.Metering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Exercises <see cref="MetersService"/> — the leaf OpenTelemetry registry for
/// push gauges, observable gauges, and counters.
/// </summary>
public sealed class MetersServiceTests
{
    private static readonly MeterMetadata Meta = new("test gauge", "{x}");

    [HumansFact]
    public void Declare_PushMeter_SetIsVisibleViaOtelCallback()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("push");
        var handle = meters.Declare(name, Meta);

        handle.Set(42);

        ReadOtelValue(name).Should().Be(42);
    }

    [HumansFact]
    public void Declare_IsIdempotentByName_ReturnsSameHandle()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("idempotent");

        var first = meters.Declare(name, Meta);
        var second = meters.Declare(name, Meta);

        second.Should().BeSameAs(first,
            because: "Declare must be safe to call from scoped services on each scope creation");
    }

    [HumansFact]
    public void Declare_MismatchedMetadata_ReturnsOriginalHandle()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("mismatch");

        var first = meters.Declare(name, new MeterMetadata("first", "{x}"));
        var second = meters.Declare(name, new MeterMetadata("second", "{y}"));

        second.Should().BeSameAs(first);
    }

    [HumansFact]
    public void Declare_MultipleMeters_StayIndependent()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var aName = UniqueName("a");
        var bName = UniqueName("b");
        var first = meters.Declare(aName, Meta);
        var second = meters.Declare(bName, Meta);

        first.Set(10);
        second.Set(20);

        ReadOtelValue(aName).Should().Be(10);
        ReadOtelValue(bName).Should().Be(20);
    }

    [HumansFact]
    public void Declare_NullMetadata_Throws()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);

        var act = () => meters.Declare(UniqueName("null-meta"), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [HumansFact]
    public void Declare_EmptyName_Throws()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);

        var act = () => meters.Declare("  ", Meta);

        act.Should().Throw<ArgumentException>();
    }

    [HumansFact]
    public void RegisterObservableGauge_Single_InvokesCallbackOnScrape()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("observable");
        var current = 0;

        meters.RegisterObservableGauge(name, Meta, () => current);

        current = 7;
        ReadOtelValue(name).Should().Be(7);
        current = 11;
        ReadOtelValue(name).Should().Be(11,
            because: "the callback is re-invoked on every scrape");
    }

    [HumansFact]
    public void RegisterObservableGauge_Multi_EmitsTaggedMeasurements()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("multi");

        meters.RegisterObservableGauge(name, Meta, () => new[]
        {
            new Measurement<int>(3, new KeyValuePair<string, object?>("status", "active")),
            new Measurement<int>(5, new KeyValuePair<string, object?>("status", "suspended")),
        });

        var measurements = ReadOtelMeasurements(name);
        measurements.Should().HaveCount(2);
        measurements.Should().ContainSingle(m => m.Value == 3 && TagValue(m.Tags, "status") == "active");
        measurements.Should().ContainSingle(m => m.Value == 5 && TagValue(m.Tags, "status") == "suspended");
    }

    [HumansFact]
    public void RegisterCounter_AddIncrementsAndExportsTags()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("counter");
        var counter = meters.RegisterCounter(name, Meta);

        var observed = new List<(long Value, string? Template)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Humans.Metrics", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, name, StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            observed.Add((value, TagValue(tags, "template")));
        });
        listener.Start();

        counter.Add(1, new KeyValuePair<string, object?>("template", "welcome"));
        counter.Add(3, new KeyValuePair<string, object?>("template", "reminder"));

        observed.Should().BeEquivalentTo(new[]
        {
            (Value: 1L, Template: "welcome"),
            (Value: 3L, Template: "reminder"),
        });
    }

    [HumansFact]
    public void RegisterCounter_IsIdempotentByName()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("counter-idem");

        var first = meters.RegisterCounter(name, Meta);
        var second = meters.RegisterCounter(name, Meta);

        second.Should().BeSameAs(first);
    }

    [HumansFact]
    public void Register_DifferentKindSameName_Throws()
    {
        using var meters = new MetersService(NullLogger<MetersService>.Instance);
        var name = UniqueName("conflict");
        meters.Declare(name, Meta);

        var registerObservable = () => meters.RegisterObservableGauge(name, Meta, () => 0);
        var registerCounter = () => meters.RegisterCounter(name, Meta);

        registerObservable.Should().Throw<InvalidOperationException>();
        registerCounter.Should().Throw<InvalidOperationException>();
    }

    // Spins up a short-lived MeterListener, triggers one measurement
    // collection, and returns the value for the named gauge. Exercises the
    // real OTel callback wired by MetersService.
    private static int ReadOtelValue(string name)
    {
        var captured = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Humans.Metrics", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, name, StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => captured = value);
        listener.Start();
        listener.RecordObservableInstruments();
        return captured;
    }

    // Same as ReadOtelValue but captures every measurement (with tags) emitted
    // for the named multi-measurement observable gauge.
    private static List<CapturedMeasurement> ReadOtelMeasurements(string name)
    {
        var captured = new List<CapturedMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Humans.Metrics", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, name, StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            captured.Add(new CapturedMeasurement(value, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();
        return captured;
    }

    private static string? TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value as string;
            }
        }
        return null;
    }

    private static string UniqueName(string tag) => $"tests.{tag}.{Guid.NewGuid():N}";

    private sealed record CapturedMeasurement(int Value, KeyValuePair<string, object?>[] Tags);
}
