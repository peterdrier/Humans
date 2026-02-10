using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
public class GoogleResourceProvisionJob
{
    private readonly IGoogleSyncService _googleService;
    private readonly ILogger<GoogleResourceProvisionJob> _logger;
    private readonly IClock _clock;

    public GoogleResourceProvisionJob(
        IGoogleSyncService googleService,
        ILogger<GoogleResourceProvisionJob> logger,
        IClock clock)
    {
        _googleService = googleService;
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
            _logger.LogInformation(
                "Successfully provisioned folder with Google ID {GoogleId}",
                resource.GoogleId);
        }
        catch (Exception ex)
        {
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
            await _googleService.SyncAllResourcesAsync(cancellationToken);
            _logger.LogInformation("Completed Google resource permission sync");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing Google resource permissions");
            throw;
        }
    }
}
