using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Startup-only hosted service that populates <see cref="IUserStore"/>
/// from the database. Runs inside <c>IHost.StartAsync()</c> which blocks
/// the Kestrel listener startup, so the brief empty-store window is
/// unreachable from HTTP.
/// </summary>
public sealed class UserStoreWarmupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserStore _store;
    private readonly ILogger<UserStoreWarmupHostedService> _logger;

    public UserStoreWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IUserStore store,
        ILogger<UserStoreWarmupHostedService> logger)
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
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            var users = await userRepository.GetAllAsync(cancellationToken);

            var cachedUsers = users.Select(CachedUser.Create).ToList();

            _store.LoadAll(cachedUsers);

            _logger.LogInformation(
                "UserStore warmed with {Count} users", cachedUsers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm UserStore at startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
