using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Services;

/// <inheritdoc cref="IHttpStatusTracker"/>
/// <remarks>
/// Runs a <see cref="MeterListener"/> over ASP.NET Core's built-in
/// <c>Microsoft.AspNetCore.Hosting</c> meter, observing the
/// <c>http.server.request.duration</c> histogram. We ignore the duration value
/// and tally one event per request, keyed by the <c>http.response.status_code</c>
/// tag. This is a passive, parallel observer — it never touches or resets the
/// instrument and does not interfere with the OpenTelemetry/Prometheus export.
/// Registered as an <see cref="IHostedService"/> so the listener attaches at
/// startup and counts from the very first request.
/// </remarks>
public sealed class HttpStatusTracker : IHttpStatusTracker, IHostedService, IDisposable
{
    private const string HostingMeterName = "Microsoft.AspNetCore.Hosting";
    private const string RequestDurationInstrument = "http.server.request.duration";
    private const string StatusCodeTag = "http.response.status_code";

    private readonly ConcurrentDictionary<int, long> _counts = new();
    private long _total;
    private MeterListener? _listener;

    public long Total => Interlocked.Read(ref _total);

    public IReadOnlyDictionary<int, long> GetCounts()
        => _counts.ToDictionary(kv => kv.Key, kv => kv.Value);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, HostingMeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, RequestDurationInstrument, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    private void OnMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        foreach (var tag in tags)
        {
            if (!string.Equals(tag.Key, StatusCodeTag, StringComparison.Ordinal))
                continue;

            if (tag.Value is int code)
            {
                _counts.AddOrUpdate(code, 1, static (_, v) => v + 1);
                Interlocked.Increment(ref _total);
            }
            return;
        }
    }

    public void Dispose() => _listener?.Dispose();
}
