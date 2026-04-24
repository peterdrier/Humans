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

    public MetersService(ILogger<MetersService> logger)
    {
        // Logger is accepted to satisfy the leaf-ILogger contract and enable
        // future diagnostic output; currently no log sites exist because the
        // registry is pure in-memory state. Keep it parameter-positioned so
        // adding logs later doesn't churn DI.
        _ = logger;
        _otelMeter = new Meter("Humans.Metrics");
    }

    public IMeter Declare(string name, MeterMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(metadata);

        return _registrations.GetOrAdd(name, _ =>
        {
            var registration = new Registration(name, owner => _registrations.TryRemove(owner.Name, out Registration? _));

            _otelMeter.CreateObservableGauge(
                name,
                observeValue: () => registration.Current,
                unit: metadata.Unit,
                description: metadata.Description);

            return registration;
        });
    }

    public void Dispose()
    {
        _otelMeter.Dispose();
    }

    private sealed class Registration : IMeter
    {
        private readonly Action<Registration> _onDispose;
        private int _current;

        public Registration(string name, Action<Registration> onDispose)
        {
            Name = name;
            _onDispose = onDispose;
        }

        public string Name { get; }
        public int Current => Volatile.Read(ref _current);

        public void Set(int value) => Volatile.Write(ref _current, value);

        public void Dispose() => _onDispose(this);
    }
}
