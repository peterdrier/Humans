using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
public class GoogleResourceProvisionJob(
    IGoogleSyncService googleService,
    IHumansMetrics metrics,
    ILogger<GoogleResourceProvisionJob> logger)
{
    /// <summary>
    /// Provisions a team folder.
    /// </summary>
    public async Task ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Provisioning team folder '{FolderName}' for team {TeamId}",
            folderName, teamId);

        try
        {
            var resource = await googleService.ProvisionTeamFolderAsync(teamId, folderName, cancellationToken);
            metrics.RecordJobRun("google_resource_provision", "success");
            logger.LogInformation(
                "Successfully provisioned folder with Google ID {GoogleId}",
                resource.GoogleId);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("google_resource_provision", "failure");
            logger.LogError(ex, "Error provisioning team folder for team {TeamId}", teamId);
            throw;
        }
    }

}
