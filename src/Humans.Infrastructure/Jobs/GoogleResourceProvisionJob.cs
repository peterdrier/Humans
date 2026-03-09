using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
public class GoogleResourceProvisionJob
{
    private readonly IGoogleSyncService _googleService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<GoogleResourceProvisionJob> _logger;
    private readonly IClock _clock;

    public GoogleResourceProvisionJob(
        IGoogleSyncService googleService,
        HumansMetricsService metrics,
        ILogger<GoogleResourceProvisionJob> logger,
        IClock clock)
    {
        _googleService = googleService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Provisions a team folder.
    /// </summary>
    public async Task ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Provisioning team folder '{FolderName}' for team {TeamId}",
            folderName, teamId);

        try
        {
            var resource = await _googleService.ProvisionTeamFolderAsync(teamId, folderName, cancellationToken);
            _metrics.RecordJobRun("google_resource_provision", "success");
            _logger.LogInformation(
                "Successfully provisioned folder with Google ID {GoogleId}",
                resource.GoogleId);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("google_resource_provision", "failure");
            _logger.LogError(ex, "Error provisioning team folder for team {TeamId}", teamId);
            throw;
        }
    }

    /// <summary>
    /// Syncs all Google resource permissions.
    /// </summary>
    public async Task SyncAllPermissionsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Google resource permission sync at {Time}", _clock.GetCurrentInstant());

        try
        {
            // TODO: Task 6 will replace with SyncResourcesByTypeAsync calls
            await Task.CompletedTask;
            _metrics.RecordJobRun("google_resource_provision", "success");
            _logger.LogInformation("Completed Google resource permission sync");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("google_resource_provision", "failure");
            _logger.LogError(ex, "Error syncing Google resource permissions");
            throw;
        }
    }
}
