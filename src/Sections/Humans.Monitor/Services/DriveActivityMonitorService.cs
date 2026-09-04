using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;
using Humans.Monitor.Contracts;
using NodaTime;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;

namespace Humans.Monitor.Services;

/// <summary>
/// Monitors Drive Activity API for non-service-account permission changes on managed resources and logs anomaly audit entries.
/// </summary>
[CrossSectionWrite("Monitor stamps its own last-run marker into the Settings key/value store.")]
internal sealed class DriveActivityMonitorService(
    IGoogleDriveActivityClient driveActivityClient,
    ITeamResourceService teamResourceService,
    ISettingsService settingsStore,
    IUserServiceRead userService,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<DriveActivityMonitorService> logger) : IDriveActivityMonitorService
{
    private const string JobName = "DriveActivityMonitorJob";

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
        var resolver = new PersonNameResolver(driveActivityClient, userService, logger);

        // Seed the resolver with the service account's client_id so that a
        // "people/{client_id}" actor reads back as the SA email.
        if (serviceAccountClientId is not null)
        {
            resolver.Seed($"people/{serviceAccountClientId}", serviceAccountEmail);
        }

        // Time-window dedup: only events since the last successful run.
        var now = clock.GetCurrentInstant();
        var lookbackTime = await GetLastRunTimestampAsync(cancellationToken)
            ?? now.Minus(Duration.FromHours(24));
        var filterTime = lookbackTime.ToIso8601();

        logger.LogDebug("Drive activity monitor checking events since {LookbackTime}", filterTime);

        var anomalies = new List<(Guid ResourceId, string Description)>();
        var hadFailures = false;
        var anyResourceQueried = false;
        Exception? firstFailure = null;

        foreach (var resource in resources)
        {
            try
            {
                await ScanResourceAsync(
                    resource, filterTime, serviceAccountEmail, serviceAccountClientId,
                    resolver, anomalies, cancellationToken);

                // Reached only if the async enumerable completed without
                // throwing — the connector is responsive for this resource.
                anyResourceQueried = true;
            }
            catch (DriveActivityResourceNotFoundException)
            {
                // Gone on Google's side, but the connector answered — still a successful
                // query for "is the connector alive".
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

        // Marker first, then audit: anomalies must surface even on a run that holds the
        // marker back.
        var newMarker = ResolveNewMarker(hadFailures, now, filterTime);
        if (newMarker is not null)
        {
            await settingsStore.SetValueAsync(
                SettingKeys.DriveActivityMonitorLastRunAt,
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
    }

    /// <summary>
    /// Appends this resource's anomalies to <paramref name="anomalies"/> as they stream, so a
    /// mid-enumeration failure keeps the ones already found.
    /// </summary>
    private async Task ScanResourceAsync(
        GoogleResourceSnapshot resource,
        string filterTime,
        string serviceAccountEmail,
        string? serviceAccountClientId,
        PersonNameResolver resolver,
        List<(Guid ResourceId, string Description)> anomalies,
        CancellationToken cancellationToken)
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

            var actorEmail = await GetActorEmailAsync(activity, resolver, cancellationToken);
            var description = await BuildAnomalyDescriptionAsync(
                activity, resource.Name, actorEmail, resolver, cancellationToken);

            logger.LogWarning(
                "Anomalous permission change detected on {ResourceName} ({GoogleId}) by {Actor}: {Description}",
                resource.Name, resource.GoogleId, actorEmail ?? "unknown", description);

            anomalies.Add((resource.Id, description));
        }
    }

    /// <summary>
    /// The marker only moves on a clean run against real credentials: a partial failure must
    /// be re-covered next time, and stub mode must not skip the history it never saw.
    /// </summary>
    private Instant? ResolveNewMarker(bool hadFailures, Instant now, string filterTime)
    {
        if (hadFailures)
        {
            logger.LogWarning(
                "Skipping last-run marker update due to partial failures — next run will re-process from {LookbackTime}",
                filterTime);
            return null;
        }

        if (!driveActivityClient.IsConfigured)
        {
            logger.LogDebug(
                "Drive activity client is not configured (stub mode) — leaving last-run marker unchanged so anomaly coverage is preserved once real credentials are configured");
            return null;
        }

        return now;
    }

    private async Task<Instant?> GetLastRunTimestampAsync(CancellationToken cancellationToken)
    {
        var value = await settingsStore.GetValueAsync(
            SettingKeys.DriveActivityMonitorLastRunAt,
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
            // for service accounts.
            if (serviceAccountClientId is not null &&
                string.Equals(actor.KnownUserPersonName, $"people/{serviceAccountClientId}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> GetActorEmailAsync(
        DriveActivityEvent activity,
        PersonNameResolver resolver,
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
                return await resolver.ResolveAsync(actor.KnownUserPersonName, cancellationToken);
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

    private static async Task<string> BuildAnomalyDescriptionAsync(
        DriveActivityEvent activity,
        string resourceName,
        string? actorEmail,
        PersonNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var permChange = activity.PermissionChange;
        var parts = new List<string>();

        if (permChange?.AddedPermissions is not null)
        {
            foreach (var perm in permChange.AddedPermissions)
            {
                parts.Add($"added {perm.Role ?? "unknown role"} for " +
                          await GetPermissionTargetAsync(perm, resolver, cancellationToken));
            }
        }

        if (permChange?.RemovedPermissions is not null)
        {
            foreach (var perm in permChange.RemovedPermissions)
            {
                parts.Add($"removed {perm.Role ?? "unknown role"} for " +
                          await GetPermissionTargetAsync(perm, resolver, cancellationToken));
            }
        }

        var changes = parts.Count > 0
            ? string.Join("; ", parts)
            : "permission change";

        return $"Anomalous permission change on '{resourceName}' by {actorEmail ?? "unknown actor"}: {changes}";
    }

    private static async Task<string> GetPermissionTargetAsync(
        DriveActivityPermission permission,
        PersonNameResolver resolver,
        CancellationToken cancellationToken)
    {
        if (permission.UserPersonName is not null)
        {
            return await resolver.ResolveAsync(permission.UserPersonName, cancellationToken);
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
    /// Turns a Drive Activity <c>people/{id}</c> name into something a human reads:
    /// cache → Admin Directory → the Users read-model → the raw id. One instance per scan —
    /// the resolved names, the Users index and the "index unavailable" latch are all per-run
    /// state, so a failed lookup is logged once and not retried for the rest of the run.
    /// </summary>
    private sealed class PersonNameResolver(
        IGoogleDriveActivityClient driveActivityClient,
        IUserServiceRead userService,
        ILogger logger)
    {
        private static readonly IReadOnlyDictionary<string, UserInfo> NoGoogleUserInfo =
            new Dictionary<string, UserInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, UserInfo>? _googleUserInfoByProviderKey;
        private bool _googleUserInfoLookupUnavailable;

        public void Seed(string personName, string email) => _resolved[personName] = email;

        public async Task<string> ResolveAsync(string personName, CancellationToken cancellationToken)
        {
            if (!personName.StartsWith("people/", StringComparison.Ordinal))
            {
                // Already an email address
                return personName;
            }

            if (_resolved.TryGetValue(personName, out var cached))
            {
                return cached;
            }

            var resolved = await driveActivityClient.TryResolvePersonEmailAsync(personName, cancellationToken);

            if (resolved is null)
            {
                // The bare id is the UserInfo ExternalLogin ProviderKey.
                var googleUserId = personName["people/".Length..];
                var byProviderKey = await GetGoogleUserInfoByProviderKeyAsync(cancellationToken);
                if (byProviderKey.TryGetValue(googleUserId, out var userInfo))
                {
                    resolved = userInfo.Email;
                }
            }

            if (resolved is not null)
            {
                _resolved[personName] = resolved;
                logger.LogDebug("Resolved {PersonName} to {Email}", personName, resolved);
                return resolved;
            }

            _resolved[personName] = personName;
            logger.LogDebug("Could not resolve {PersonName} to an email address", personName);
            return personName;
        }

        private async Task<IReadOnlyDictionary<string, UserInfo>> GetGoogleUserInfoByProviderKeyAsync(
            CancellationToken cancellationToken)
        {
            if (_googleUserInfoByProviderKey is not null)
            {
                return _googleUserInfoByProviderKey;
            }

            if (_googleUserInfoLookupUnavailable)
            {
                return NoGoogleUserInfo;
            }

            try
            {
                _googleUserInfoByProviderKey = await LoadAsync(cancellationToken);
                return _googleUserInfoByProviderKey;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _googleUserInfoLookupUnavailable = true;
                logger.LogWarning(
                    ex,
                    "Could not load UserInfo Google login fallback for Drive Activity people ids; unresolved ids will be left raw for this run");
                return NoGoogleUserInfo;
            }
        }

        /// <summary>
        /// A provider key claimed by two humans resolves to neither: the raw id is the honest
        /// answer, and naming the wrong human in an anomaly entry is the failure to avoid.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, UserInfo>> LoadAsync(CancellationToken cancellationToken)
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
}
