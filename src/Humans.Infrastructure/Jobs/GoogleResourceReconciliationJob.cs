using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Nightly job that reconciles all Google resources (Groups + Drive folders)
/// with the expected state from the database.
/// </summary>
public class GoogleResourceReconciliationJob
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ILogger<GoogleResourceReconciliationJob> _logger;
    private readonly IClock _clock;

    public GoogleResourceReconciliationJob(
        IGoogleSyncService googleSyncService,
        ILogger<GoogleResourceReconciliationJob> logger,
        IClock clock)
    {
        _googleSyncService = googleSyncService;
        _logger = logger;
        _clock = clock;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting nightly Google resource reconciliation at {Time}", _clock.GetCurrentInstant());

        try
        {
            await _googleSyncService.SyncAllResourcesAsync(cancellationToken);
            _logger.LogInformation("Completed nightly Google resource reconciliation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during nightly Google resource reconciliation");
            throw;
        }
    }
}
