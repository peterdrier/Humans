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
/// which every declared gauge is automatically OpenTelemetry-exported through
/// the existing <c>AddMeter("Humans.Metrics")</c> subscription in
/// <c>Program.cs</c>.
/// </summary>
public sealed class MetersService : IMeters, IDisposable
{
    private readonly Meter _otelMeter;
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
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

        var registration = _registrations.GetOrAdd(name, n =>
        {
            var reg = new Registration(n, metadata);

            _otelMeter.CreateObservableGauge(
                n,
                observeValue: () => reg.Current,
                unit: metadata.Unit,
                description: metadata.Description);

            _logger.LogDebug(
                "Registered gauge {GaugeName} (unit={Unit}, description={Description})",
                n, metadata.Unit, metadata.Description);

            return reg;
        });

        if (registration.Metadata != metadata)
        {
            _logger.LogWarning(
                "Gauge {GaugeName} re-declared with different metadata; keeping original. " +
                "Original: unit={OriginalUnit}, description={OriginalDescription}. " +
                "Ignored: unit={IgnoredUnit}, description={IgnoredDescription}",
                name,
                registration.Metadata.Unit, registration.Metadata.Description,
                metadata.Unit, metadata.Description);
        }

        return registration;
    }

    public void Dispose()
    {
        _otelMeter.Dispose();
    }

    private sealed class Registration : IMeter
    {
        private int _current;

        public Registration(string name, MeterMetadata metadata)
        {
            Name = name;
            Metadata = metadata;
        }

        public string Name { get; }
        public MeterMetadata Metadata { get; }
        public int Current => Volatile.Read(ref _current);

        public void Set(int value) => Volatile.Write(ref _current, value);
    }
}
