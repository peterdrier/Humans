using Humans.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.Agent.Services;

namespace Humans.Agent.Services.Stores;

internal sealed class AgentSettingsStoreWarmupHostedService(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentSettingsService>();
        await service.LoadAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
