using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Singleton <see cref="IMeters"/>. Leaf node in the DI graph — only dependency
/// is <see cref="ILogger{TCategoryName}"/>. Owns a single
/// <c>System.Diagnostics.Metrics.Meter("Humans.Metrics")</c> instrument under
/// which every registered gauge and counter is automatically OpenTelemetry-exported
/// through the existing <c>AddMeter("Humans.Metrics")</c> subscription in
/// <c>Program.cs</c>. Observable-gauge callbacks are invoked by OpenTelemetry at
/// scrape time; there is no separate refresh tick.
/// </summary>
public sealed class MetersService : IMeters, IDisposable
{
    private readonly Meter _otelMeter;
    private readonly ConcurrentDictionary<string, InstrumentEntry> _instruments = new(StringComparer.Ordinal);
    private readonly ILogger<MetersService> _logger;

    public MetersService(ILogger<MetersService> logger)
    {
        _logger = logger;
        _otelMeter = new Meter("Humans.Metrics");
    }

    public IMeter Declare(string name, MeterMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(metadata);

        var entry = _instruments.GetOrAdd(name, n =>
        {
            var handle = new PushGaugeHandle();
            _otelMeter.CreateObservableGauge(
                n,
                observeValue: () => handle.Current,
                unit: metadata.Unit,
                description: metadata.Description);
            LogRegistered("push gauge", n, metadata);
            return new InstrumentEntry(metadata, InstrumentKind.PushGauge, handle);
        });

        AssertKind(entry, InstrumentKind.PushGauge, name);
        WarnIfMetadataDiffers(entry, metadata, name);
        return (IMeter)entry.Handle!;
    }

    public void RegisterObservableGauge(string name, MeterMetadata metadata, Func<int> observe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(observe);

        var entry = _instruments.GetOrAdd(name, n =>
        {
            _otelMeter.CreateObservableGauge(
                n,
                observeValue: observe,
                unit: metadata.Unit,
                description: metadata.Description);
            LogRegistered("observable gauge", n, metadata);
            return new InstrumentEntry(metadata, InstrumentKind.ObservableGauge, handle: null);
        });

        AssertKind(entry, InstrumentKind.ObservableGauge, name);
        WarnIfMetadataDiffers(entry, metadata, name);
    }

    public void RegisterObservableGauge(
        string name,
        MeterMetadata metadata,
        Func<IEnumerable<Measurement<int>>> observe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(observe);

        var entry = _instruments.GetOrAdd(name, n =>
        {
            _otelMeter.CreateObservableGauge(
                n,
                observeValues: observe,
                unit: metadata.Unit,
                description: metadata.Description);
            LogRegistered("observable gauge (multi)", n, metadata);
            return new InstrumentEntry(metadata, InstrumentKind.ObservableGauge, handle: null);
        });

        AssertKind(entry, InstrumentKind.ObservableGauge, name);
        WarnIfMetadataDiffers(entry, metadata, name);
    }

    public Counter<long> RegisterCounter(string name, MeterMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(metadata);

        var entry = _instruments.GetOrAdd(name, n =>
        {
            var counter = _otelMeter.CreateCounter<long>(
                n,
                unit: metadata.Unit,
                description: metadata.Description);
            LogRegistered("counter", n, metadata);
            return new InstrumentEntry(metadata, InstrumentKind.Counter, counter);
        });

        AssertKind(entry, InstrumentKind.Counter, name);
        WarnIfMetadataDiffers(entry, metadata, name);
        return (Counter<long>)entry.Handle!;
    }

    public void Dispose()
    {
        _otelMeter.Dispose();
    }

    private void LogRegistered(string kind, string name, MeterMetadata metadata)
    {
        _logger.LogDebug(
            "Registered {Kind} {Name} (unit={Unit}, description={Description})",
            kind, name, metadata.Unit, metadata.Description);
    }

    private void WarnIfMetadataDiffers(InstrumentEntry entry, MeterMetadata metadata, string name)
    {
        if (entry.Metadata != metadata)
        {
            _logger.LogWarning(
                "Instrument {Name} re-registered with different metadata; keeping original. " +
                "Original: unit={OriginalUnit}, description={OriginalDescription}. " +
                "Ignored: unit={IgnoredUnit}, description={IgnoredDescription}",
                name,
                entry.Metadata.Unit, entry.Metadata.Description,
                metadata.Unit, metadata.Description);
        }
    }

    private static void AssertKind(InstrumentEntry entry, InstrumentKind expected, string name)
    {
        if (entry.Kind != expected)
        {
            throw new InvalidOperationException(
                $"Instrument '{name}' is already registered as a {entry.Kind} but was requested as a {expected}.");
        }
    }

    private enum InstrumentKind
    {
        PushGauge,
        ObservableGauge,
        Counter,
    }

    private sealed class InstrumentEntry
    {
        public InstrumentEntry(MeterMetadata metadata, InstrumentKind kind, object? handle)
        {
            Metadata = metadata;
            Kind = kind;
            Handle = handle;
        }

        public MeterMetadata Metadata { get; }
        public InstrumentKind Kind { get; }
        public object? Handle { get; }
    }

    private sealed class PushGaugeHandle : IMeter
    {
        private int _current;

        public int Current => Volatile.Read(ref _current);

        public void Set(int value) => Volatile.Write(ref _current, value);
    }
}
