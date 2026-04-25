using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Profile section's gauges with <see cref="IMeters"/> at app
/// startup, and refreshes their values from the section's services every 60
/// seconds. OTel observable callbacks read the cached fields synchronously.
/// </summary>
public sealed class ProfileMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProfileMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _pendingVolunteers;
    private volatile int _pendingConsents;
    private volatile int _consentDeadlineApproaching;
    private int _activeCount;
    private int _suspendedCount;
    private int _pendingCount;
    private int _inactiveCount;

    public ProfileMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<ProfileMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.humans_total",
            new MeterMetadata("Total humans by status", "{humans}"),
            ObserveHumansByStatus);

        _meters.RegisterObservableGauge(
            "humans.pending_volunteers",
            new MeterMetadata("Volunteers awaiting board approval", "{volunteers}"),
            () => _pendingVolunteers);

        _meters.RegisterObservableGauge(
            "humans.pending_consents",
            new MeterMetadata("Users missing required consents", "{users}"),
            () => _pendingConsents);

        _meters.RegisterObservableGauge(
            "humans.consent_deadline_approaching",
            new MeterMetadata("Users past grace period not yet suspended", "{users}"),
            () => _consentDeadlineApproaching);

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

    private IEnumerable<Measurement<int>> ObserveHumansByStatus()
    {
        yield return new Measurement<int>(
            Volatile.Read(ref _activeCount),
            new KeyValuePair<string, object?>("status", "active"));
        yield return new Measurement<int>(
            Volatile.Read(ref _suspendedCount),
            new KeyValuePair<string, object?>("status", "suspended"));
        yield return new Measurement<int>(
            Volatile.Read(ref _pendingCount),
            new KeyValuePair<string, object?>("status", "pending"));
        yield return new Measurement<int>(
            Volatile.Read(ref _inactiveCount),
            new KeyValuePair<string, object?>("status", "inactive"));
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var profileService = scope.ServiceProvider.GetRequiredService<IProfileService>();
            var membershipCalculator = scope.ServiceProvider.GetRequiredService<IMembershipCalculator>();

            var allUserIds = await userService.GetAllUserIdsAsync();
            var profiles = await profileService.GetByUserIdsAsync(allUserIds);

            int active = 0, suspended = 0, pending = 0, inactive = 0;
            foreach (var userId in allUserIds)
            {
                if (profiles.TryGetValue(userId, out var profile))
                {
                    if (profile.IsSuspended)
                        suspended++;
                    else if (!profile.IsApproved)
                        pending++;
                    else
                        active++;
                }
                else
                {
                    inactive++;
                }
            }

            Volatile.Write(ref _activeCount, active);
            Volatile.Write(ref _suspendedCount, suspended);
            Volatile.Write(ref _pendingCount, pending);
            Volatile.Write(ref _inactiveCount, inactive);

            _pendingVolunteers = await profileService.GetNotApprovedAndNotSuspendedCountAsync();

            var withConsents = await membershipCalculator.GetUsersWithAllRequiredConsentsAsync(allUserIds);
            _pendingConsents = allUserIds.Count - withConsents.Count;

            var requiringUpdate = await membershipCalculator.GetUsersRequiringStatusUpdateAsync();
            _consentDeadlineApproaching = requiringUpdate.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Profile section metrics");
        }
    }
}
