using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that provisions Google Drive resources.
/// </summary>
public class GoogleResourceProvisionJob
{
    private readonly IGoogleSyncService _googleService;
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<GoogleResourceProvisionJob> _logger;

    public GoogleResourceProvisionJob(
        IGoogleSyncService googleService,
        IMeters meters,
        ILogger<GoogleResourceProvisionJob> logger)
    {
        _googleService = googleService;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "google_resource_provision"),
                new KeyValuePair<string, object?>("result", "success"));
            _logger.LogInformation(
                "Successfully provisioned folder with Google ID {GoogleId}",
                resource.GoogleId);
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "google_resource_provision"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error provisioning team folder for team {TeamId}", teamId);
            throw;
        }
    }

}
