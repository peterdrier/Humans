using Hangfire;
using Humans.Application.Interfaces.Admin;
using Humans.Web.Infrastructure;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Humans.Web.Services;

/// <summary>
/// Composes a system-health snapshot from two runtime sources:
/// <list type="bullet">
///   <item><see cref="InMemoryLogSink"/> — recent Error/Fatal log events</item>
///   <item>Hangfire monitoring API — count of failed background jobs</item>
/// </list>
/// Placed in <c>Humans.Web</c> because both dependencies (<c>InMemoryLogSink</c>
/// and Hangfire's <c>JobStorage</c>) live at the Web layer.
/// </summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly ILogger<AdminDashboardService> _logger;

    public AdminDashboardService(ILogger<AdminDashboardService> logger)
    {
        _logger = logger;
    }

    public Task<AdminSystemHealth> GetSystemHealthAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var events = InMemoryLogSink.Instance.GetEvents(200);
        var errors = CountErrorsSince(events, since);

        int failed = 0;
        try
        {
            failed = (int)JobStorage.Current.GetMonitoringApi().FailedCount();
        }
        catch (Exception ex)
        {
            // Hangfire may not be initialized (e.g. test environment that skips Postgres
            // storage in Program.cs); treat failed count as 0 and surface the reason.
            _logger.LogDebug(ex, "Hangfire monitoring API not available; reporting 0 failed jobs");
        }

        return Task.FromResult(new AdminSystemHealth(errors, failed));
    }

    /// <summary>
    /// Counts log events at Error or Fatal level that occurred on or after
    /// <paramref name="since"/>. Extracted for unit-testability.
    /// </summary>
    public static int CountErrorsSince(IReadOnlyList<LogEvent> events, DateTimeOffset since) =>
        events.Count(e =>
            (e.Level == LogEventLevel.Error || e.Level == LogEventLevel.Fatal)
            && e.Timestamp >= since);
}
