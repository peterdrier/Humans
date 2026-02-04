using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs legal documents from the GitHub repository.
/// </summary>
public class SyncLegalDocumentsJob
{
    private readonly ILegalDocumentSyncService _syncService;
    private readonly IEmailService _emailService;
    private readonly ILogger<SyncLegalDocumentsJob> _logger;
    private readonly IClock _clock;

    public SyncLegalDocumentsJob(
        ILegalDocumentSyncService syncService,
        IEmailService emailService,
        ILogger<SyncLegalDocumentsJob> logger,
        IClock clock)
    {
        _syncService = syncService;
        _emailService = emailService;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Executes the legal document sync job.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting legal document sync at {Time}", _clock.GetCurrentInstant());

        try
        {
            var updatedDocs = await _syncService.SyncAllDocumentsAsync(cancellationToken);

            if (updatedDocs.Count > 0)
            {
                _logger.LogInformation(
                    "Synced {Count} updated legal documents: {Documents}",
                    updatedDocs.Count,
                    string.Join(", ", updatedDocs.Select(d => d.Name)));

                // TODO: Trigger re-consent notifications for members
            }
            else
            {
                _logger.LogInformation("No legal document updates found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing legal documents");
            throw;
        }
    }
}
