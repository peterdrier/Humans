using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
public class GoogleResourceProvisionJob
{
    private readonly IGoogleSyncService _googleService;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<GoogleResourceProvisionJob> _logger;

    public GoogleResourceProvisionJob(
        IGoogleSyncService googleService,
        IHumansMetrics metrics,
        ILogger<GoogleResourceProvisionJob> logger)
    {
        _googleService = googleService;
        _metrics = metrics;
        _logger = logger;
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

}
