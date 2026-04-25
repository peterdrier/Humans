using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Auth section's <c>humans.role_assignments_active</c> gauge
/// (multi-measurement, tag <c>role</c>) with <see cref="IMeters"/> at app
/// startup. A 60-second tick refreshes the cached counts via
/// <see cref="IRoleAssignmentService"/>; OTel observable callbacks read the
/// cached snapshot synchronously.
/// </summary>
public sealed class AuthMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyList<(string Role, int Count)> EmptySnapshot = [];

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile IReadOnlyList<(string Role, int Count)> _snapshot = EmptySnapshot;

    public AuthMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<AuthMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.role_assignments_active",
            new MeterMetadata("Active role assignments by role", "{assignments}"),
            ObserveRoleAssignments);

        _refreshTimer = new Timer(
            callback: _ => _ = RefreshAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: RefreshInterval);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    private IEnumerable<Measurement<int>> ObserveRoleAssignments()
    {
        foreach (var (role, count) in _snapshot)
        {
            yield return new Measurement<int>(
                count,
                new KeyValuePair<string, object?>("role", role));
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var roleAssignmentService = scope.ServiceProvider
                .GetRequiredService<IRoleAssignmentService>();

            var counts = await roleAssignmentService.GetActiveCountsByRoleAsync();
            _snapshot = counts.Select(c => (c.RoleName, c.Count)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Auth section metrics");
        }
    }
}
