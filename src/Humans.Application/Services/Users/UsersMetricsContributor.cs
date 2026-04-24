using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Users;

/// <summary>
/// Users section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Gauge-only, no counters. Owns <c>humans.pending_deletions</c>.
/// </summary>
public sealed class UsersMetricsContributor : IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private volatile int _pendingDeletions;

    public UsersMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        metrics.CreateObservableGauge<int>(
            "humans.pending_deletions",
            observeValue: () => _pendingDeletions,
            description: "Accounts scheduled for deletion");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        _pendingDeletions = await userService.GetPendingDeletionCountAsync(cancellationToken);
    }
}
