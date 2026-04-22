using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Monitors Google Drive Activity API for permission changes on managed
/// resources that were not initiated by the system's service account, and
/// records an anomaly audit-log entry for every such change.
/// </summary>
/// <remarks>
/// <para>
/// Migrated to the §15 pattern under issue #554 (Google Integration). The
/// Application-layer service now depends only on shape-neutral interfaces:
/// <see cref="IGoogleDriveActivityClient"/> for Google API access,
/// <see cref="ITeamResourceService"/> for the list of monitored resources,
/// and <see cref="IDriveActivityMonitorRepository"/> for the last-run
/// marker, anomaly audit-entry persistence, and the local-DB fallback that
/// resolves a <c>people/{id}</c> to a user's email.
/// </para>
/// <para>
/// The service no longer imports <c>Google.Apis.*</c> or
/// <c>Microsoft.EntityFrameworkCore</c> — see the architecture tests for the
/// compile-time guarantees.
/// </para>
/// </remarks>
public sealed class DriveActivityMonitorService : IDriveActivityMonitorService
{
    private readonly IGoogleDriveActivityClient _driveActivityClient;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IDriveActivityMonitorRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<DriveActivityMonitorService> _logger;

    private const string JobName = "DriveActivityMonitorJob";

    public DriveActivityMonitorService(
        IGoogleDriveActivityClient driveActivityClient,
        ITeamResourceService teamResourceService,
        IDriveActivityMonitorRepository repository,
        IClock clock,
        ILogger<DriveActivityMonitorService> logger)
    {
        _driveActivityClient = driveActivityClient;
        _teamResourceService = teamResourceService;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default)
    {
        var resources = await _teamResourceService.GetActiveDriveFoldersAsync(cancellationToken);

        if (resources.Count == 0)
        {
            _logger.LogDebug("No active Drive folder resources to monitor");
            return 0;
        }

        var serviceAccountEmail = await _driveActivityClient.GetServiceAccountEmailAsync(cancellationToken);
        var serviceAccountClientId = await _driveActivityClient.GetServiceAccountClientIdAsync(cancellationToken);
        var hadFailures = false;

        // Per-invocation cache for resolved people/ IDs to avoid repeated API calls.
        var peopleIdCache = new Dictionary<string, string>(StringComparer.Ordinal);

        // Seed the people ID cache with the service account's client_id so that
        // ResolvePersonNameAsync maps "people/{client_id}" back to the SA email.
        if (serviceAccountClientId is not null)
        {
            peopleIdCache[$"people/{serviceAccountClientId}"] = serviceAccountEmail;
        }

        // Use time-window dedup: only process events since the last successful run.
        // Falls back to 24 hours on first run or if the stored timestamp is missing.
        var now = _clock.GetCurrentInstant();
        var lookbackTime = await _repository.GetLastRunTimestampAsync(cancellationToken)
            ?? now.Minus(Duration.FromHours(24));
        var filterTime = lookbackTime.ToInvariantInstantString();

        _logger.LogDebug("Drive activity monitor checking events since {LookbackTime}", filterTime);

        var anomalies = new List<AuditLogEntry>();

        foreach (var resource in resources)
        {
            try
            {
                await foreach (var activity in _driveActivityClient.QueryActivityAsync(
                                   resource.GoogleId, filterTime, cancellationToken))
                {
                    if (activity.PermissionChange is null)
                    {
                        continue;
                    }

                    if (IsInitiatedByServiceAccount(activity, serviceAccountEmail, serviceAccountClientId))
                    {
                        continue;
                    }

                    var description = await BuildAnomalyDescriptionAsync(
                        activity, resource.Name, peopleIdCache, cancellationToken);
                    var actorEmail = await GetActorEmailAsync(
                        activity, peopleIdCache, cancellationToken);

                    _logger.LogWarning(
                        "Anomalous permission change detected on {ResourceName} ({GoogleId}) by {Actor}: {Description}",
                        resource.Name, resource.GoogleId, actorEmail ?? "unknown", description);

                    anomalies.Add(new AuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        Action = AuditAction.AnomalousPermissionDetected,
                        EntityType = nameof(GoogleResource),
                        EntityId = resource.Id,
                        Description = $"{JobName}: {description}",
                        OccurredAt = _clock.GetCurrentInstant(),
                        ActorUserId = null,
                    });
                }
            }
            catch (DriveActivityResourceNotFoundException)
            {
                _logger.LogWarning(
                    "Drive resource {GoogleId} not found when checking activity (may have been deleted)",
                    resource.GoogleId);
            }
            catch (Exception ex)
            {
                hadFailures = true;
                _logger.LogError(ex, "Error checking Drive activity for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        if (anomalies.Count > 0)
        {
            _logger.LogWarning(
                "Detected {AnomalyCount} anomalous permission change(s) across {ResourceCount} resources",
                anomalies.Count, resources.Count);
        }
        else
        {
            _logger.LogInformation(
                "Drive activity check completed: no anomalous changes detected across {ResourceCount} resources",
                resources.Count);
        }

        // Only advance the last-run marker when every resource was processed successfully
        // AND the client is actually backed by real credentials. If any resource failed,
        // keep the old marker so the next run re-checks those events. When the client is
        // the stub (no Google credentials), advancing would silently skip every historical
        // permission change that happens before the same database later gains real
        // credentials — so never advance in stub mode regardless of "success".
        Instant? newMarker;
        if (hadFailures)
        {
            newMarker = null;
            _logger.LogWarning(
                "Skipping last-run marker update due to partial failures — next run will re-process from {LookbackTime}",
                filterTime);
        }
        else if (!_driveActivityClient.IsConfigured)
        {
            newMarker = null;
            _logger.LogDebug(
                "Drive activity client is not configured (stub mode) — leaving last-run marker unchanged so anomaly coverage is preserved once real credentials are configured");
        }
        else
        {
            newMarker = now;
        }

        await _repository.PersistAnomaliesAsync(anomalies, newMarker, cancellationToken);

        return anomalies.Count;
    }

    private static bool IsInitiatedByServiceAccount(
        DriveActivityEvent activity, string serviceAccountEmail, string? serviceAccountClientId)
    {
        if (activity.Actors.Count == 0)
        {
            return false;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.KnownUserPersonName is null)
            {
                continue;
            }

            // The personName field may contain the SA email directly
            if (string.Equals(actor.KnownUserPersonName, serviceAccountEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Drive Activity API often returns "people/{client_id}" instead of the email
            // for service accounts. Match against the SA's client_id.
            if (serviceAccountClientId is not null &&
                string.Equals(actor.KnownUserPersonName, $"people/{serviceAccountClientId}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> GetActorEmailAsync(
        DriveActivityEvent activity,
        Dictionary<string, string> peopleIdCache,
        CancellationToken cancellationToken)
    {
        if (activity.Actors.Count == 0)
        {
            return null;
        }

        foreach (var actor in activity.Actors)
        {
            if (actor.KnownUserPersonName is not null)
            {
                return await ResolvePersonNameAsync(actor.KnownUserPersonName, peopleIdCache, cancellationToken);
            }

            if (actor.IsAdministrator)
            {
                return "Google Workspace Admin";
            }

            if (actor.IsSystem)
            {
                return "Google System";
            }
        }

        return null;
    }

    private async Task<string> BuildAnomalyDescriptionAsync(
        DriveActivityEvent activity,
        string resourceName,
        Dictionary<string, string> peopleIdCache,
        CancellationToken cancellationToken)
    {
        var actorEmail = await GetActorEmailAsync(activity, peopleIdCache, cancellationToken) ?? "unknown actor";
        var permChange = activity.PermissionChange;
        var parts = new List<string>();

        if (permChange?.AddedPermissions is not null)
        {
            foreach (var perm in permChange.AddedPermissions)
            {
                var target = await GetPermissionTargetAsync(perm, peopleIdCache, cancellationToken);
                var role = perm.Role ?? "unknown role";
                parts.Add($"added {role} for {target}");
            }
        }

        if (permChange?.RemovedPermissions is not null)
        {
            foreach (var perm in permChange.RemovedPermissions)
            {
                var target = await GetPermissionTargetAsync(perm, peopleIdCache, cancellationToken);
                var role = perm.Role ?? "unknown role";
                parts.Add($"removed {role} for {target}");
            }
        }

        var changes = parts.Count > 0
            ? string.Join("; ", parts)
            : "permission change";

        return $"Anomalous permission change on '{resourceName}' by {actorEmail}: {changes}";
    }

    private async Task<string> GetPermissionTargetAsync(
        DriveActivityPermission permission,
        Dictionary<string, string> peopleIdCache,
        CancellationToken cancellationToken)
    {
        if (permission.UserPersonName is not null)
        {
            return await ResolvePersonNameAsync(permission.UserPersonName, peopleIdCache, cancellationToken);
        }

        if (permission.GroupEmail is not null)
        {
            return $"group:{permission.GroupEmail}";
        }

        if (permission.DomainName is not null)
        {
            return $"domain:{permission.DomainName}";
        }

        if (permission.IsAnyone)
        {
            return "anyone";
        }

        return "unknown";
    }

    /// <summary>
    /// Resolves a Drive Activity API person name to an email address.
    /// If the name is a people/ resource ID (e.g., "people/123456789"), attempts resolution via:
    /// 1. Per-invocation cache
    /// 2. Google Admin Directory API (via the connector)
    /// 3. Local DB lookup (matching Google user IDs)
    /// Falls back to the raw ID if all resolution fails.
    /// </summary>
    private async Task<string> ResolvePersonNameAsync(
        string personName,
        Dictionary<string, string> peopleIdCache,
        CancellationToken cancellationToken)
    {
        if (!personName.StartsWith("people/", StringComparison.Ordinal))
        {
            // Already an email address
            return personName;
        }

        if (peopleIdCache.TryGetValue(personName, out var cached))
        {
            return cached;
        }

        // Extract the numeric user ID from "people/123456789" for the local-DB fallback.
        var googleUserId = personName["people/".Length..];

        var resolved = await _driveActivityClient.TryResolvePersonEmailAsync(personName, cancellationToken)
            ?? await _repository.TryResolveEmailByGoogleUserIdAsync(googleUserId, cancellationToken);

        if (resolved is not null)
        {
            peopleIdCache[personName] = resolved;
            _logger.LogDebug("Resolved {PersonName} to {Email}", personName, resolved);
            return resolved;
        }

        // Fall back to raw ID
        peopleIdCache[personName] = personName;
        _logger.LogDebug("Could not resolve {PersonName} to an email address", personName);
        return personName;
    }
}
