using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Humans.Infrastructure.Services.Metering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Exercises <see cref="MetersService"/> — the leaf OpenTelemetry gauge registry.
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

    private static string UniqueName(string tag) => $"tests.{tag}.{Guid.NewGuid():N}";
}
