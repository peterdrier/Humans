using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.DriveActivity.v2;
using Google.Apis.DriveActivity.v2.Data;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Monitors Google Drive Activity API for permission changes on managed resources
/// that were not initiated by the system's service account.
/// </summary>
public class DriveActivityMonitorService : IDriveActivityMonitorService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly GoogleWorkspaceSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<DriveActivityMonitorService> _logger;

    private DriveActivityService? _activityService;
    private string? _serviceAccountEmail;

    private const string JobName = "DriveActivityMonitorJob";

    public DriveActivityMonitorService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IOptions<GoogleWorkspaceSettings> settings,
        IClock clock,
        ILogger<DriveActivityMonitorService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default)
    {
        var resources = await _dbContext.GoogleResources
            .Where(r => r.IsActive && r.ResourceType == GoogleResourceType.DriveFolder)
            .ToListAsync(cancellationToken);

        if (resources.Count == 0)
        {
            _logger.LogDebug("No active Drive folder resources to monitor");
            return 0;
        }

        var activityService = await GetActivityServiceAsync();
        var serviceAccountEmail = await GetServiceAccountEmailAsync(cancellationToken);
        var anomalyCount = 0;

        // Check activity from the last 24 hours
        var lookbackTime = _clock.GetCurrentInstant().Minus(Duration.FromHours(24));
        var filterTime = lookbackTime.ToString(null, System.Globalization.CultureInfo.InvariantCulture);

        foreach (var resource in resources)
        {
            try
            {
                var anomalies = await CheckResourceActivityAsync(
                    activityService, resource.GoogleId, resource.Id, resource.Name,
                    serviceAccountEmail, filterTime, lookbackTime, cancellationToken);
                anomalyCount += anomalies;
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
            {
                _logger.LogWarning("Drive resource {GoogleId} not found when checking activity (may have been deleted)",
                    resource.GoogleId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Drive activity for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        if (anomalyCount > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Detected {AnomalyCount} anomalous permission change(s) across {ResourceCount} resources",
                anomalyCount, resources.Count);
        }
        else
        {
            _logger.LogInformation("Drive activity check completed: no anomalous changes detected across {ResourceCount} resources",
                resources.Count);
        }

        return anomalyCount;
    }

    private async Task<int> CheckResourceActivityAsync(
        DriveActivityService activityService,
        string googleDriveId,
        Guid resourceId,
        string resourceName,
        string serviceAccountEmail,
        string filterTime,
        Instant lookbackInstant,
        CancellationToken cancellationToken)
    {
        var anomalyCount = 0;
        string? pageToken = null;

        do
        {
            var request = new QueryDriveActivityRequest
            {
                ItemName = $"items/{googleDriveId}",
                Filter = $"time >= \"{filterTime}\"",
                PageSize = 100,
                PageToken = pageToken
            };

            var queryRequest = activityService.Activity.Query(request);
            var response = await queryRequest.ExecuteAsync(cancellationToken);

            if (response.Activities != null)
            {
                foreach (var activity in response.Activities)
                {
                    if (IsPermissionChangeActivity(activity) &&
                        !IsInitiatedByServiceAccount(activity, serviceAccountEmail))
                    {
                        var description = BuildAnomalyDescription(activity, resourceName);

                        // Skip if this exact anomaly was already logged within the lookback window
                        var alreadyLogged = await _dbContext.AuditLogEntries
                            .AsNoTracking()
                            .AnyAsync(e => e.Action == AuditAction.AnomalousPermissionDetected
                                && e.EntityId == resourceId
                                && e.Description == description
                                && e.OccurredAt >= lookbackInstant, cancellationToken);

                        if (alreadyLogged)
                        {
                            continue;
                        }

                        var actorEmail = GetActorEmail(activity);

                        _logger.LogWarning(
                            "Anomalous permission change detected on {ResourceName} ({GoogleId}) by {Actor}: {Description}",
                            resourceName, googleDriveId, actorEmail ?? "unknown", description);

                        await _auditLogService.LogAsync(
                            AuditAction.AnomalousPermissionDetected,
                            "GoogleResource",
                            resourceId,
                            description,
                            JobName);

                        anomalyCount++;
                    }
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return anomalyCount;
    }

    private static bool IsPermissionChangeActivity(DriveActivity activity)
    {
        if (activity.PrimaryActionDetail == null)
        {
            return false;
        }

        return activity.PrimaryActionDetail.PermissionChange != null;
    }

    private static bool IsInitiatedByServiceAccount(DriveActivity activity, string serviceAccountEmail)
    {
        if (activity.Actors == null || activity.Actors.Count == 0)
        {
            return false;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.User?.KnownUser?.PersonName != null)
            {
                // The personName field contains the user's email in Drive Activity API
                if (string.Equals(actor.User.KnownUser.PersonName, serviceAccountEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? GetActorEmail(DriveActivity activity)
    {
        if (activity.Actors == null || activity.Actors.Count == 0)
        {
            return null;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.User?.KnownUser?.PersonName != null)
            {
                return actor.User.KnownUser.PersonName;
            }

            if (actor.Administrator != null)
            {
                return "Google Workspace Admin";
            }

            if (actor.System != null)
            {
                return "Google System";
            }
        }

        return null;
    }

    private static string BuildAnomalyDescription(DriveActivity activity, string resourceName)
    {
        var actorEmail = GetActorEmail(activity) ?? "unknown actor";
        var permChange = activity.PrimaryActionDetail?.PermissionChange;
        var parts = new List<string>();

        if (permChange?.AddedPermissions != null)
        {
            foreach (var perm in permChange.AddedPermissions)
            {
                var target = GetPermissionTarget(perm);
                var role = perm.Role ?? "unknown role";
                parts.Add($"added {role} for {target}");
            }
        }

        if (permChange?.RemovedPermissions != null)
        {
            foreach (var perm in permChange.RemovedPermissions)
            {
                var target = GetPermissionTarget(perm);
                var role = perm.Role ?? "unknown role";
                parts.Add($"removed {role} for {target}");
            }
        }

        var changes = parts.Count > 0
            ? string.Join("; ", parts)
            : "permission change";

        return $"Anomalous permission change on '{resourceName}' by {actorEmail}: {changes}";
    }

    private static string GetPermissionTarget(Permission permission)
    {
        if (permission.User?.KnownUser?.PersonName != null)
        {
            return permission.User.KnownUser.PersonName;
        }

        if (permission.Group?.Email != null)
        {
            return $"group:{permission.Group.Email}";
        }

        if (permission.Domain?.Name != null)
        {
            return $"domain:{permission.Domain.Name}";
        }

        if (permission.Anyone != null)
        {
            return "anyone";
        }

        return "unknown";
    }

    private async Task<DriveActivityService> GetActivityServiceAsync()
    {
        if (_activityService != null)
        {
            return _activityService;
        }

        var credential = await GetCredentialAsync();

        _activityService = new DriveActivityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _activityService;
    }

    private async Task<GoogleCredential> GetCredentialAsync()
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            await using var stream = System.IO.File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        return credential.CreateScoped(DriveActivityService.Scope.DriveActivityReadonly);
    }

    private async Task<string> GetServiceAccountEmailAsync(CancellationToken ct)
    {
        if (_serviceAccountEmail != null)
        {
            return _serviceAccountEmail;
        }

        _serviceAccountEmail = await ExtractServiceAccountEmailAsync(ct);
        return _serviceAccountEmail;
    }

    private async Task<string> ExtractServiceAccountEmailAsync(CancellationToken ct)
    {
        string? json = null;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            json = _settings.ServiceAccountKeyJson;
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            json = await System.IO.File.ReadAllTextAsync(_settings.ServiceAccountKeyPath, ct);
        }

        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
            {
                return emailElement.GetString() ?? "unknown@serviceaccount.iam.gserviceaccount.com";
            }
        }

        return "unknown@serviceaccount.iam.gserviceaccount.com";
    }
}
