using System.Diagnostics.Metrics;
using Humans.Auth.Contracts;
using Humans.Base.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Auth.Services;

/// <summary>
/// Owns the Auth-section observable gauge split out of <c>HumansMetricsService</c>
/// (nobodies-collective/Humans#1091): active role-assignment counts by role.
/// </summary>
internal sealed class AuthMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile IReadOnlyList<(string Role, int Count)> _snapshot = [];

    public AuthMetricsService(IServiceScopeFactory scopeFactory, ILogger<AuthMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.role_assignments_active",
            observeValues: ObserveRoleAssignments,
            description: "Active role assignments by role");
    }

    private IEnumerable<Measurement<int>> ObserveRoleAssignments()
    {
        foreach (var (role, count) in _snapshot)
        {
            yield return new Measurement<int>(count, new KeyValuePair<string, object?>("role", role));
        }
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        var counts = await roleAssignmentService.GetActiveCountsByRoleAsync();
        _snapshot = counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
