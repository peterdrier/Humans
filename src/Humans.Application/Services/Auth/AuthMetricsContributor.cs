using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Auth;

/// <summary>
/// Auth section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Gauge-only. Owns <c>humans.role_assignments_active</c> (tagged by role).
/// </summary>
public sealed class AuthMetricsContributor : IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private IReadOnlyList<(string Role, int Count)> _snapshot = Array.Empty<(string, int)>();

    public AuthMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        metrics.CreateObservableGauge<int>(
            "humans.role_assignments_active",
            observeValues: ObserveRoleAssignments,
            description: "Active role assignments by role");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    private IEnumerable<Measurement<int>> ObserveRoleAssignments()
    {
        // Read the volatile reference once per scrape so the snapshot cannot
        // flip mid-enumeration.
        var snapshot = _snapshot;
        foreach (var (role, count) in snapshot)
        {
            yield return new Measurement<int>(count, new KeyValuePair<string, object?>("role", role));
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        _snapshot = await roleAssignmentService.GetActiveCountsByRoleAsync(cancellationToken);
    }
}
