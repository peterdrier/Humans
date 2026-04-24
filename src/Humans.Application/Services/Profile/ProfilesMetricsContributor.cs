using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Profiles section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Gauge-only, no counters. Owns:
/// <list type="bullet">
///   <item><c>humans.humans_total</c> — by status (active/suspended/pending/inactive).</item>
///   <item><c>humans.pending_volunteers</c> — pending onboarding profiles.</item>
/// </list>
/// "Inactive" is the set of users with no profile row; the contributor reads the
/// total user count from <see cref="IUserService"/> and partitions the profile
/// status via <see cref="IProfileService"/>. Both reads go through owning-section
/// services — no direct DB access.
/// </summary>
public sealed class ProfilesMetricsContributor : IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private volatile int _activeCount;
    private volatile int _suspendedCount;
    private volatile int _pendingCount;
    private volatile int _inactiveCount;

    public ProfilesMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        metrics.CreateObservableGauge<int>(
            "humans.humans_total",
            observeValues: ObserveHumansTotal,
            description: "Total humans by status");

        metrics.CreateObservableGauge<int>(
            "humans.pending_volunteers",
            observeValue: () => _pendingCount,
            description: "Volunteers awaiting board approval");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    private IEnumerable<Measurement<int>> ObserveHumansTotal()
    {
        yield return new Measurement<int>(_activeCount, new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(_suspendedCount, new KeyValuePair<string, object?>("status", "suspended"));
        yield return new Measurement<int>(_pendingCount, new KeyValuePair<string, object?>("status", "pending"));
        yield return new Measurement<int>(_inactiveCount, new KeyValuePair<string, object?>("status", "inactive"));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var profileService = scope.ServiceProvider.GetRequiredService<IProfileService>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var statusCounts = await profileService.GetProfileStatusCountsAsync(cancellationToken);
        var allUserIds = await userService.GetAllUserIdsAsync(cancellationToken);

        var totalProfiles = statusCounts.Approved + statusCounts.Suspended + statusCounts.Pending;

        _activeCount = statusCounts.Approved;
        _suspendedCount = statusCounts.Suspended;
        _pendingCount = statusCounts.Pending;
        // Inactive = users without a profile row.
        _inactiveCount = Math.Max(0, allUserIds.Count - totalProfiles);
    }
}
