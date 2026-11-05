using Hangfire;
using Humans.Holded.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Holded.Jobs;

/// <summary>
/// Queues one <see cref="HoldedSyncJob"/> at boot while the purchase-document or contact mirror has never synced, so a fresh
/// deployment shows Finance's actuals and unmatched documents within minutes instead of after the 03:00 run.
/// Migrations finish in <c>StartingAsync</c>, before this runs.
/// </summary>
internal sealed class HoldedMirrorCatchUp(
    IServiceScopeFactory scopes,
    IBackgroundJobClient jobs,
    IOptions<HoldedClientOptions> options,
    ILogger<HoldedMirrorCatchUp> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.Value.ApiKey)) return;

        using var scope = scopes.CreateScope();
        var holded = scope.ServiceProvider.GetRequiredService<IHoldedService>();
        if ((await holded.GetPurchaseDocumentsAsync(cancellationToken)).LastSyncAt is not null
            && await holded.GetContactsAsync(cancellationToken) is not null) return;

        jobs.Enqueue<HoldedSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        logger.LogInformation("Holded purchase-document or contact mirror has never synced; queued a sync");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
