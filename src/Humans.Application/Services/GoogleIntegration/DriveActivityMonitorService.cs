using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.SystemSettings;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Monitors Drive Activity API for non-service-account permission changes on managed resources and logs anomaly audit entries.
/// </summary>
public sealed class DriveActivityMonitorService(
    IGoogleDriveActivityClient driveActivityClient,
    ITeamResourceService teamResourceService,
    ISystemSettingsService systemSettings,
    IUserServiceRead userService,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<DriveActivityMonitorService> logger) : IDriveActivityMonitorService
{
    private const string JobName = "DriveActivityMonitorJob";
    private static readonly IReadOnlyDictionary<string, UserInfo> EmptyGoogleUserInfoByProviderKey =
        new Dictionary<string, UserInfo>(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default)
    {
        var resources = await teamResourceService.GetActiveDriveFoldersAsync(cancellationToken);

        if (resources.Count == 0)
        {
            logger.LogDebug("No active Drive folder resources to monitor");
            return 0;
        }

        var serviceAccountEmail = await driveActivityClient.GetServiceAccountEmailAsync(cancellationToken);
        var serviceAccountClientId = await driveActivityClient.GetServiceAccountClientIdAsync(cancellationToken);
        var hadFailures = false;
        var anyResourceQueried = false;
        Exception? firstFailure = null;

        // Per-invocation cache for resolved people/ IDs to avoid repeated API calls.
        var peopleIdCache = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, UserInfo>? googleUserInfoByProviderKey = null;
        var googleUserInfoLookupUnavailable = false;

        // Seed the people ID cache with the service account's client_id so that
        // ResolvePersonNameAsync maps "people/{client_id}" back to the SA email.
        if (serviceAccountClientId is not null)
        {
            peopleIdCache[$"people/{serviceAccountClientId}"] = serviceAccountEmail;
        }

        // Use time-window dedup: only process events since the last successful run.
        // Falls back to 24 hours on first run or if the stored timestamp is missing.
        var now = clock.GetCurrentInstant();
        var lookbackTime = await GetLastRunTimestampAsync(cancellationToken)
            ?? now.Minus(Duration.FromHours(24));
        var filterTime = lookbackTime.ToIso8601();

        logger.LogDebug("Drive activity monitor checking events since {LookbackTime}", filterTime);

        var anomalies = new List<(Guid ResourceId, string Description)>();

        foreach (var resource in resources)
        {
            try
            {
                await foreach (var activity in driveActivityClient.QueryActivityAsync(
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
                        activity, resource.Name, peopleIdCache,
                        GetGoogleUserInfoByProviderKeyAsync, cancellationToken);
                    var actorEmail = await GetActorEmailAsync(
                        activity, peopleIdCache,
                        GetGoogleUserInfoByProviderKeyAsync, cancellationToken);

                    logger.LogWarning(
                        "Anomalous permission change detected on {ResourceName} ({GoogleId}) by {Actor}: {Description}",
                        resource.Name, resource.GoogleId, actorEmail ?? "unknown", description);

                    anomalies.Add((resource.Id, description));
                }

                // Reached only if the async enumerable completed without
                // throwing — the connector is responsive for this resource.
                anyResourceQueried = true;
            }
            catch (DriveActivityResourceNotFoundException)
            {
                // Resource exists in our DB but is gone on Google's side.
                // The connector itself worked, so this still counts as a
                // successful query for "is the connector alive" purposes.
                anyResourceQueried = true;
                logger.LogWarning(
                    "Drive resource {GoogleId} not found when checking activity (may have been deleted)",
                    resource.GoogleId);
            }
            catch (Exception ex)
            {
                hadFailures = true;
                firstFailure ??= ex;
                logger.LogError(ex, "Error checking Drive activity for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        if (anomalies.Count > 0)
        {
            logger.LogWarning(
                "Detected {AnomalyCount} anomalous permission change(s) across {ResourceCount} resources",
                anomalies.Count, resources.Count);
        }
        else
        {
            logger.LogInformation(
                "Drive activity check completed: no anomalous changes detected across {ResourceCount} resources",
                resources.Count);
        }

        // Only advance marker on full success with real credentials — stub mode never advances (would skip historical changes).
        Instant? newMarker;
        if (hadFailures)
        {
            newMarker = null;
            logger.LogWarning(
                "Skipping last-run marker update due to partial failures — next run will re-process from {LookbackTime}",
                filterTime);
        }
        else if (!driveActivityClient.IsConfigured)
        {
            newMarker = null;
            logger.LogDebug(
                "Drive activity client is not configured (stub mode) — leaving last-run marker unchanged so anomaly coverage is preserved once real credentials are configured");
        }
        else
        {
            newMarker = now;
        }

        // Persist the monitor's own state (the last-run marker) first, then emit
        // the audit entries through IAuditLogService so the only writer of
        // audit_log_entries is the AuditLog section's repository. Audit is logged
        // after the business save (per IAuditLogService) and regardless of the
        // marker outcome — anomalies must surface even on a partial-failure run.
        if (newMarker is not null)
        {
            await systemSettings.SetValueAsync(
                SystemSettingKeys.DriveActivityMonitorLastRunAt,
                newMarker.Value.ToIso8601(),
                cancellationToken);
        }

        foreach (var (resourceId, description) in anomalies)
        {
            await auditLogService.LogAsync(
                AuditAction.AnomalousPermissionDetected,
                nameof(GoogleResource),
                resourceId,
                description,
                JobName);
        }

        // All-resources-failed = connector outage (revoked key / network). Throw so Hangfire records a failed run, not a hollow success.
        if (hadFailures && !anyResourceQueried)
        {
            throw new InvalidOperationException(
                $"Drive activity monitor: all {resources.Count} resource(s) failed to query; connector is likely unavailable. See inner exception for the first failure.",
                firstFailure);
        }

        return anomalies.Count;

        async Task<IReadOnlyDictionary<string, UserInfo>> GetGoogleUserInfoByProviderKeyAsync()
        {
            if (googleUserInfoByProviderKey is not null)
            {
                return googleUserInfoByProviderKey;
            }

            if (googleUserInfoLookupUnavailable)
            {
                return EmptyGoogleUserInfoByProviderKey;
            }

            try
            {
                googleUserInfoByProviderKey = await LoadGoogleUserInfoByProviderKeyAsync(cancellationToken);
                return googleUserInfoByProviderKey;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                googleUserInfoLookupUnavailable = true;
                logger.LogWarning(
                    ex,
                    "Could not load UserInfo Google login fallback for Drive Activity people ids; unresolved ids will be left raw for this run");
                return EmptyGoogleUserInfoByProviderKey;
            }
        }
    }

    private async Task<Instant?> GetLastRunTimestampAsync(CancellationToken cancellationToken)
    {
        var value = await systemSettings.GetValueAsync(
            SystemSettingKeys.DriveActivityMonitorLastRunAt,
            cancellationToken);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var pattern = NodaTime.Text.InstantPattern.General;
        var result = pattern.Parse(value);
        if (result.Success)
        {
            return result.Value;
        }

        logger.LogWarning(
            "Could not parse stored Drive activity monitor timestamp '{Value}', falling back to default lookback",
            value);
        return null;
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
        Func<Task<IReadOnlyDictionary<string, UserInfo>>> getGoogleUserInfoByProviderKeyAsync,
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
                return await ResolvePersonNameAsync(
                    actor.KnownUserPersonName,
                    peopleIdCache,
                    getGoogleUserInfoByProviderKeyAsync,
                    cancellationToken);
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
        Func<Task<IReadOnlyDictionary<string, UserInfo>>> getGoogleUserInfoByProviderKeyAsync,
        CancellationToken cancellationToken)
    {
        var actorEmail = await GetActorEmailAsync(
            activity, peopleIdCache, getGoogleUserInfoByProviderKeyAsync, cancellationToken) ?? "unknown actor";
        var permChange = activity.PermissionChange;
        var parts = new List<string>();

        if (permChange?.AddedPermissions is not null)
        {
            foreach (var perm in permChange.AddedPermissions)
            {
                var target = await GetPermissionTargetAsync(
                    perm, peopleIdCache, getGoogleUserInfoByProviderKeyAsync, cancellationToken);
                var role = perm.Role ?? "unknown role";
                parts.Add($"added {role} for {target}");
            }
        }

        if (permChange?.RemovedPermissions is not null)
        {
            foreach (var perm in permChange.RemovedPermissions)
            {
                var target = await GetPermissionTargetAsync(
                    perm, peopleIdCache, getGoogleUserInfoByProviderKeyAsync, cancellationToken);
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
        Func<Task<IReadOnlyDictionary<string, UserInfo>>> getGoogleUserInfoByProviderKeyAsync,
        CancellationToken cancellationToken)
    {
        if (permission.UserPersonName is not null)
        {
            return await ResolvePersonNameAsync(
                permission.UserPersonName,
                peopleIdCache,
                getGoogleUserInfoByProviderKeyAsync,
                cancellationToken);
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
    /// Resolves a "people/{id}" name to an email via cache → Admin Directory → UserInfo. Falls back to raw id.
    /// </summary>
    private async Task<string> ResolvePersonNameAsync(
        string personName,
        Dictionary<string, string> peopleIdCache,
        Func<Task<IReadOnlyDictionary<string, UserInfo>>> getGoogleUserInfoByProviderKeyAsync,
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

        // Extract the numeric user ID from "people/123456789" for the UserInfo fallback.
        var googleUserId = personName["people/".Length..];

        var resolved = await driveActivityClient.TryResolvePersonEmailAsync(personName, cancellationToken);

        if (resolved is null)
        {
            var googleUserInfoByProviderKey = await getGoogleUserInfoByProviderKeyAsync();
            if (googleUserInfoByProviderKey.TryGetValue(googleUserId, out var userInfo))
            {
                resolved = userInfo.Email;
            }
        }

        if (resolved is not null)
        {
            peopleIdCache[personName] = resolved;
            logger.LogDebug("Resolved {PersonName} to {Email}", personName, resolved);
            return resolved;
        }

        // Fall back to raw ID
        peopleIdCache[personName] = personName;
        logger.LogDebug("Could not resolve {PersonName} to an email address", personName);
        return personName;
    }

    private async Task<IReadOnlyDictionary<string, UserInfo>> LoadGoogleUserInfoByProviderKeyAsync(
        CancellationToken cancellationToken)
    {
        var userInfos = await userService.GetAllUserInfosAsync(cancellationToken);
        var result = new Dictionary<string, UserInfo>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var userInfo in userInfos)
        {
            foreach (var login in userInfo.ExternalLogins)
            {
                if (!string.Equals(login.Provider, "Google", StringComparison.Ordinal))
                    continue;

                if (userInfo.Email is null)
                    continue;

                if (ambiguous.Contains(login.ProviderKey))
                    continue;

                if (result.TryAdd(login.ProviderKey, userInfo))
                    continue;

                result.Remove(login.ProviderKey);
                ambiguous.Add(login.ProviderKey);
                logger.LogWarning(
                    "Skipping ambiguous Google provider key {ProviderKey} while resolving Drive Activity people id",
                    login.ProviderKey);
            }
        }

        return result;
    }
}
