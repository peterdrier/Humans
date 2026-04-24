using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Singleton that owns the <c>Humans.Metrics</c> <see cref="Meter"/>, the
/// counter/gauge creation API, and the 60-second gauge-snapshot refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// Pure infrastructure: after issue nobodies-collective/Humans#580 this service
/// knows nothing about Teams, Applications, Users, or any other section. Sections
/// register their own counters and gauges via <see cref="IMetricsContributor"/>
/// — the contributors are resolved at construction and <see cref="IMetricsContributor.Initialize"/>
/// is invoked exactly once per contributor with this service passed as the
/// <see cref="IHumansMetrics"/> registration surface.
/// </para>
/// <para>
/// The 60-second tick iterates over all section-registered refresh callbacks in
/// parallel. Each callback is wrapped in its own try/catch so a single section's
/// exception cannot blank other sections' gauges for that cycle — the error is
/// logged and the next tick retries.
/// </para>
/// <para>
/// This service stays in <c>Humans.Infrastructure</c> (like
/// <c>IGoogleDriveClient</c>) because it legitimately owns process-wide OTel
/// infrastructure — the <see cref="Meter"/> and <see cref="Timer"/> resources —
/// and has no business logic of its own.
/// </para>
/// </remarks>
public sealed class HumansMetricsService : IHumansMetrics, IDisposable
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<HumansMetricsService> _logger;
    private readonly List<Func<CancellationToken, Task>> _refreshers = [];
    private readonly Timer _refreshTimer;
    private readonly CancellationTokenSource _shutdownCts = new();

    public HumansMetricsService(
        IEnumerable<IMetricsContributor> contributors,
        ILogger<HumansMetricsService> logger)
    {
        _logger = logger;

        // Initialise every section's counters / gauges / refreshers BEFORE the
        // first tick runs. Contributors are resolved eagerly (AddSingleton) so
        // the DI container constructs them in time; the registry calls
        // Initialize once on each, passing itself as the IHumansMetrics surface.
        foreach (var contributor in contributors)
        {
            try
            {
                contributor.Initialize(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Metrics contributor {Type} failed to Initialize; its counters/gauges will be missing",
                    contributor.GetType().Name);
            }
        }

        // Fire the first refresh immediately (on the thread pool) and then every
        // 60 seconds thereafter so gauges have fresh values on first scrape.
        _refreshTimer = new Timer(
            callback: _ => _ = RunRefreshersAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: RefreshInterval);
    }

    public Counter<T> CreateCounter<T>(string name, string? description = null) where T : struct =>
        HumansMeter.CreateCounter<T>(name, description: description);

    public void CreateObservableGauge<T>(string name, Func<T> observeValue, string? description = null)
        where T : struct
    {
        HumansMeter.CreateObservableGauge<T>(
            name,
            observeValue: observeValue,
            description: description);
    }

    public void CreateObservableGauge<T>(
        string name,
        Func<IEnumerable<Measurement<T>>> observeValues,
        string? description = null) where T : struct
    {
        HumansMeter.CreateObservableGauge<T>(
            name,
            observeValues: observeValues,
            description: description);
    }

    public void RegisterGaugeRefresher(Func<CancellationToken, Task> refresher)
    {
        ArgumentNullException.ThrowIfNull(refresher);
        _refreshers.Add(refresher);
    }

    private async Task RunRefreshersAsync()
    {
        // Per-refresher try/catch ensures one section's failure does not break
        // other sections' gauges for the same tick. Run in parallel — at 500
        // users scale the per-refresher DB work is tiny.
        var ct = _shutdownCts.Token;
        if (ct.IsCancellationRequested) return;

        var tasks = _refreshers.Select(r => RunOneAsync(r, ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task RunOneAsync(Func<CancellationToken, Task> refresher, CancellationToken ct)
    {
        try
        {
            await refresher(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Metrics gauge refresher failed on the snapshot tick; gauges for this contributor will retain their previous values until the next cycle");
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _refreshTimer.Dispose();
        _shutdownCts.Dispose();
    }
}
