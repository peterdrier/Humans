using Hangfire;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that pulls Holded purchase docs into the local read-side store.
/// Runs daily at 04:30 UTC by default. Can also be triggered manually from the admin UI.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class HoldedSyncJob : IRecurringJob
{
    private readonly IHoldedSyncService _syncService;
    private readonly HoldedSettings _settings;
    private readonly ILogger<HoldedSyncJob> _logger;

    public HoldedSyncJob(
        IHoldedSyncService syncService,
        IOptions<HoldedSettings> settings,
        ILogger<HoldedSyncJob> logger)
    {
        _syncService = syncService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogDebug("Holded not configured (Enabled=false or no API key); skipping scheduled sync");
            return;
        }

        _logger.LogInformation("Starting Holded sync job");

        try
        {
            var result = await _syncService.SyncAsync(cancellationToken);

            _logger.LogInformation(
                "Holded sync completed: {Fetched} docs ({Matched} matched, {Unmatched} unmatched)",
                result.DocsFetched, result.Matched, result.Unmatched);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Holded sync job failed");
            throw;
        }
    }
}
