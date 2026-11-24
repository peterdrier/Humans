using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Auth.Services;

internal sealed class AuthMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile IReadOnlyList<(string Role, int Count)> _snapshot = [];

    public AuthMetricsService(IServiceScopeFactory scopeFactory, ILogger<AuthMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.system_roles_active",
            observeValues: ObserveSystemRoles,
            description: "Active system roles by role");
    }

    private IEnumerable<Measurement<int>> ObserveSystemRoles()
    {
        foreach (var (role, count) in _snapshot)
        {
            yield return new Measurement<int>(count, new KeyValuePair<string, object?>("role", role));
        }
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var systemRoleService = scope.ServiceProvider.GetRequiredService<ISystemRoleManagement>();
        var counts = await systemRoleService.GetActiveCountsByRoleAsync();
        _snapshot = counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
