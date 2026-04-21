using Humans.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Infrastructure.Stores;

public sealed class AgentSettingsStoreWarmupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public AgentSettingsStoreWarmupHostedService(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentSettingsService>();
        await service.LoadAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
