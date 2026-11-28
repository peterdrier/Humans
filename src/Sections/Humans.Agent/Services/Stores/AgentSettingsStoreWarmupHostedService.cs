using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Agent.Services.Stores;

internal sealed class AgentSettingsStoreWarmupHostedService(
    IServiceScopeFactory scopes,
    ILogger<AgentSettingsStoreWarmupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAgentSettingsService>();
            await service.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Agent settings warmup failed; keeping disabled defaults until settings are saved");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
