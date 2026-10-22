using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Humans.Base.Interfaces;

namespace Humans.Web.Services;

/// <inheritdoc cref="IHttpStatusTracker"/>
/// <remarks>
/// A passive <see cref="MeterListener"/> over ASP.NET Core's <c>http.server.request.duration</c> histogram, tallying
/// one event per request by <c>http.response.status_code</c>. It never resets the instrument, so the
/// OpenTelemetry/Prometheus export is unaffected.
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
