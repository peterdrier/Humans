using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Populates <see cref="IApplicationStore"/> from the repository once before
/// the host starts accepting requests. Runs inside an ad-hoc DI scope so
/// the scoped <see cref="IApplicationRepository"/> (which wraps the scoped
/// <c>HumansDbContext</c>) can be resolved.
/// </summary>
/// <remarks>
/// If the initial load fails, the host fails to start — intentional. At
/// ~500-user scale a warmup failure means DB connectivity is broken, which
/// would fail every request anyway; surfacing it at startup is safer than
/// silently serving stale (empty) store reads.
/// </remarks>
public sealed class ApplicationStoreWarmupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationStore _store;
    private readonly ILogger<ApplicationStoreWarmupHostedService> _logger;

    public ApplicationStoreWarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IApplicationStore store,
        ILogger<ApplicationStoreWarmupHostedService> logger)
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
            var repository = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();

            var all = await repository.GetAllAsync(cancellationToken);
            _store.LoadAll(all);

            _logger.LogInformation(
                "ApplicationStore warmed with {Count} applications",
                all.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm ApplicationStore at startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
