using System.Text.Json;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.DriveActivity.v2;
using Google.Apis.DriveActivity.v2.Data;
using Google.Apis.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
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
    private DirectoryService? _directoryService;
    private string? _serviceAccountEmail;

    /// <summary>
    /// Per-invocation cache for resolved people/ IDs to avoid repeated API calls.
    /// </summary>
    private readonly Dictionary<string, string> _peopleIdCache = new(StringComparer.Ordinal);

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
        var filterTime = lookbackTime.ToInvariantInstantString();

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

            if (response.Activities is not null)
            {
                foreach (var activity in response.Activities)
                {
                    if (IsPermissionChangeActivity(activity) &&
                        !IsInitiatedByServiceAccount(activity, serviceAccountEmail))
                    {
                        var description = await BuildAnomalyDescriptionAsync(activity, resourceName, cancellationToken);

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

                        var actorEmail = await GetActorEmailAsync(activity, cancellationToken);

                        _logger.LogWarning(
                            "Anomalous permission change detected on {ResourceName} ({GoogleId}) by {Actor}: {Description}",
                            resourceName, googleDriveId, actorEmail ?? "unknown", description);

                        await _auditLogService.LogAsync(
                            AuditAction.AnomalousPermissionDetected,
                            nameof(GoogleResource),
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
        if (activity.PrimaryActionDetail is null)
        {
            return false;
        }

        return activity.PrimaryActionDetail.PermissionChange is not null;
    }

    private static bool IsInitiatedByServiceAccount(DriveActivity activity, string serviceAccountEmail)
    {
        if (activity.Actors is null || activity.Actors.Count == 0)
        {
            return false;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.User?.KnownUser?.PersonName is not null)
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

    private async Task<string?> GetActorEmailAsync(DriveActivity activity, CancellationToken cancellationToken)
    {
        if (activity.Actors is null || activity.Actors.Count == 0)
        {
            return null;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.User?.KnownUser?.PersonName is not null)
            {
                return await ResolvePersonNameAsync(actor.User.KnownUser.PersonName, cancellationToken);
            }

            if (actor.Administrator is not null)
            {
                return "Google Workspace Admin";
            }

            if (actor.System is not null)
            {
                return "Google System";
            }
        }

        return null;
    }

    private async Task<string> BuildAnomalyDescriptionAsync(
        DriveActivity activity, string resourceName, CancellationToken cancellationToken)
    {
        var actorEmail = await GetActorEmailAsync(activity, cancellationToken) ?? "unknown actor";
        var permChange = activity.PrimaryActionDetail?.PermissionChange;
        var parts = new List<string>();

        if (permChange?.AddedPermissions is not null)
        {
            foreach (var perm in permChange.AddedPermissions)
            {
                var target = await GetPermissionTargetAsync(perm, cancellationToken);
                var role = perm.Role ?? "unknown role";
                parts.Add($"added {role} for {target}");
            }
        }

        if (permChange?.RemovedPermissions is not null)
        {
            foreach (var perm in permChange.RemovedPermissions)
            {
                var target = await GetPermissionTargetAsync(perm, cancellationToken);
                var role = perm.Role ?? "unknown role";
                parts.Add($"removed {role} for {target}");
            }
        }

        var changes = parts.Count > 0
            ? string.Join("; ", parts)
            : "permission change";

        return $"Anomalous permission change on '{resourceName}' by {actorEmail}: {changes}";
    }

    private async Task<string> GetPermissionTargetAsync(Permission permission, CancellationToken cancellationToken)
    {
        if (permission.User?.KnownUser?.PersonName is not null)
        {
            return await ResolvePersonNameAsync(permission.User.KnownUser.PersonName, cancellationToken);
        }

        if (permission.Group?.Email is not null)
        {
            return $"group:{permission.Group.Email}";
        }

        if (permission.Domain?.Name is not null)
        {
            return $"domain:{permission.Domain.Name}";
        }

        if (permission.Anyone is not null)
        {
            return "anyone";
        }

        return "unknown";
    }

    /// <summary>
    /// Resolves a Drive Activity API person name to an email address.
    /// If the name is a people/ resource ID (e.g., "people/123456789"), attempts resolution via:
    /// 1. Per-invocation cache
    /// 2. Google Admin Directory API
    /// 3. Local DB lookup (matching Google user IDs)
    /// Falls back to the raw ID if all resolution fails.
    /// </summary>
    private async Task<string> ResolvePersonNameAsync(string personName, CancellationToken cancellationToken)
    {
        if (!personName.StartsWith("people/", StringComparison.Ordinal))
        {
            // Already an email address
            return personName;
        }

        if (_peopleIdCache.TryGetValue(personName, out var cached))
        {
            return cached;
        }

        var resolved = await TryResolveViaDirectoryApiAsync(personName, cancellationToken)
            ?? await TryResolveViaLocalDbAsync(personName, cancellationToken);

        if (resolved is not null)
        {
            _peopleIdCache[personName] = resolved;
            _logger.LogDebug("Resolved {PersonName} to {Email}", personName, resolved);
            return resolved;
        }

        // Fall back to raw ID
        _peopleIdCache[personName] = personName;
        _logger.LogDebug("Could not resolve {PersonName} to an email address", personName);
        return personName;
    }

    /// <summary>
    /// Attempts to resolve a people/ ID to an email via the Admin Directory API.
    /// The people/ ID from Drive Activity API corresponds to a Google user ID.
    /// </summary>
    private async Task<string?> TryResolveViaDirectoryApiAsync(string personName, CancellationToken cancellationToken)
    {
        try
        {
            var directoryService = await GetDirectoryServiceAsync();
            // Extract the numeric user ID from "people/123456789"
            var userId = personName["people/".Length..];
            var userRequest = directoryService.Users.Get(userId);
            var user = await userRequest.ExecuteAsync(cancellationToken);
            return user?.PrimaryEmail;
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
        {
            _logger.LogDebug("Directory API could not resolve {PersonName} (HTTP {Code})",
                personName, ex.Error.Code);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving {PersonName} via Directory API", personName);
            return null;
        }
    }

    /// <summary>
    /// Attempts to resolve a people/ ID by looking up the user in the local database.
    /// Users authenticated via Google OAuth have their Google user ID stored in login claims.
    /// </summary>
    private async Task<string?> TryResolveViaLocalDbAsync(string personName, CancellationToken cancellationToken)
    {
        try
        {
            // Extract the numeric user ID from "people/123456789"
            var googleUserId = personName["people/".Length..];

            // Check ASP.NET Identity user logins for Google provider
            var login = await _dbContext.Set<IdentityUserLogin<Guid>>()
                .AsNoTracking()
                .FirstOrDefaultAsync(l =>
                    l.ProviderKey == googleUserId &&
                    l.LoginProvider == "Google",
                    cancellationToken);

            if (login is not null)
            {
                var user = await _dbContext.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == login.UserId, cancellationToken);
                return user?.Email;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving {PersonName} via local DB", personName);
            return null;
        }
    }

    private async Task<DriveActivityService> GetActivityServiceAsync()
    {
        if (_activityService is not null)
        {
            return _activityService;
        }

        var credential = await GetCredentialAsync(DriveActivityService.Scope.DriveActivityReadonly);

        _activityService = new DriveActivityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _activityService;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService is not null)
        {
            return _directoryService;
        }

        var credential = await GetCredentialAsync(DirectoryService.Scope.AdminDirectoryUserReadonly);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
    }

    private async Task<GoogleCredential> GetCredentialAsync(params string[] scopes)
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

        return credential.CreateScoped(scopes);
    }

    private async Task<string> GetServiceAccountEmailAsync(CancellationToken ct)
    {
        if (_serviceAccountEmail is not null)
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

        if (json is not null)
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
