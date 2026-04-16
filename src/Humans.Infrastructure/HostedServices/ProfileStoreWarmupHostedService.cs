using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Startup-only hosted service that populates <see cref="IProfileStore"/>
/// from the database. Runs inside <c>IHost.StartAsync()</c> which blocks
/// the Kestrel listener startup, so the brief empty-store window is
/// unreachable from HTTP.
/// </summary>
public sealed class ProfileStoreWarmupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProfileStore _store;
    private readonly ILogger<ProfileStoreWarmupHostedService> _logger;

    public ProfileStoreWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IProfileStore store,
        ILogger<ProfileStoreWarmupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var profileRepository = scope.ServiceProvider.GetRequiredService<IProfileRepository>();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

            // Load all profiles with aggregate-local collections
            var profiles = await profileRepository.GetAllAsync(cancellationToken);

            // Load user display data for stitching into CachedProfile
            var userIds = profiles.Select(p => p.UserId).ToList();
            var users = await userService.GetByIdsAsync(userIds, cancellationToken);

            // Build CachedProfile entries keyed by UserId
            var entries = new Dictionary<Guid, CachedProfile>(profiles.Count);
            foreach (var profile in profiles)
            {
                if (!users.TryGetValue(profile.UserId, out var user))
                    continue;

                entries[profile.UserId] = CachedProfile.Create(profile, user);
            }

            _store.LoadAll(entries);

            _logger.LogInformation(
                "ProfileStore warmed with {Count} profiles", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm ProfileStore at startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
