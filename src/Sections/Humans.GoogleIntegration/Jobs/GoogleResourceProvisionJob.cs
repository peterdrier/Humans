using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at the G5 jobs move
/// (nobodies-collective/Humans#866). It has no enqueue site anywhere in <c>src/</c> — see the
/// 2026-08-05 debt-ledger entry, which is still open on "wire it or delete it". The move keeps
/// it exactly as dead as it was; it does not settle that question.
/// </remarks>
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
