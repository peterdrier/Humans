using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Google.Data;
using Humans.Google.Services.Workspace;
using Humans.Teams.Contracts;

namespace Humans.Google.Services;

internal sealed class GoogleGroupSettings(
    IGoogleGroupProvisioningClient groupProvisioning,
    IGoogleDirectoryClient directory,
    IGoogleResourceRepository resourceRepository,
    ITeamService teamService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    GoogleWorkspaceOptions groupOptions,
    ILogger logger) : IApplicationService
{
    public async Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogInformation("Google Groups sync is disabled — skipping settings drift check");
            return new GroupSettingsDriftResult
            {
                Skipped = true,
                SkipReason = "Google Groups sync mode is set to None"
            };
        }

        var groupResources = await resourceRepository.GetActiveByResourceTypeAsync(GoogleResourceType.Group, cancellationToken);

        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var filtered = groupResources
            .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        logger.LogInformation("Checking group settings for {Count} active Google Groups", filtered.Count);

        var reports = new List<GroupSettingsDriftReport>();
        foreach (var resource in filtered)
        {
            var groupEmail = GoogleGroupKeyHelper.TryGetGroupKey(
                resource, teamsById[resource.TeamId].GoogleGroupEmail, groupOptions.Domain);

            if (string.IsNullOrEmpty(groupEmail))
            {
                reports.Add(new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = "Cannot determine group email address"
                });
                continue;
            }

            var report = await CheckSingleGroupSettingsAsync(resource, groupEmail, cancellationToken);
            reports.Add(report);
        }

        return new GroupSettingsDriftResult
        {
            Reports = reports,
            ExpectedSettings = GroupSettingsPolicy.BuildExpectedSettingsDictionary(groupOptions.Groups)
        };
    }

    private async Task<GroupSettingsDriftReport> CheckSingleGroupSettingsAsync(
        TeamGoogleResource resource,
        string groupEmail,
        CancellationToken cancellationToken)
    {
        var getResult = await groupProvisioning.GetGroupSettingsAsync(groupEmail, cancellationToken);
        if (getResult.Settings is null)
        {
            var code = getResult.Error?.StatusCode ?? 0;
            if (code == 404 || code == 403)
            {
                logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", groupEmail, code);
                return new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupEmail = groupEmail,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}"
                };
            }
            logger.LogWarning(
                "Error fetching settings for group '{GroupEmail}' — HTTP {Code}: {Message}",
                groupEmail, code, getResult.Error?.RawMessage);
            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                ErrorMessage = $"Error: {getResult.Error?.RawMessage}"
            };
        }

        var drifts = new List<GroupSettingDrift>();
        var expected = GroupSettingsPolicy.BuildExpectedSettingsDictionary(groupOptions.Groups);
        var actualDict = GroupSettingsPolicy.SnapshotToEnforcedDict(getResult.Settings);

        foreach (var (key, expectedValue) in expected)
            GroupSettingsPolicy.CompareGroupSetting(drifts, key, expectedValue, actualDict.GetValueOrDefault(key));

        if (drifts.Count > 0)
        {
            logger.LogWarning("Group '{GroupEmail}' has {DriftCount} setting drift(s): {Drifts}",
                groupEmail, drifts.Count,
                string.Join(", ", drifts.Select(d => $"{d.SettingName}: expected={d.ExpectedValue}, actual={d.ActualValue}")));
        }

        return new GroupSettingsDriftReport
        {
            ResourceId = resource.Id,
            GroupEmail = groupEmail,
            GroupName = resource.Name,
            Url = resource.Url,
            Drifts = drifts
        };
    }

    public async Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            // Settings remediation is always allowed — it doesn't add/remove members.
            var error = await groupProvisioning.UpdateGroupSettingsAsync(
                groupEmail, GroupSettingsPolicy.BuildExpected(groupOptions.Groups), cancellationToken);

            if (error is not null)
            {
                logger.LogError(
                    "Failed to remediate settings for Google Group {GroupEmail} — HTTP {Code}: {Message}",
                    groupEmail, error.StatusCode, error.RawMessage);
                return GroupSettingsRemediationResult.Failure(
                    $"Google Groups settings update failed for {groupEmail}: HTTP {error.StatusCode} — {error.RawMessage}");
            }

            await auditLogService.LogAsync(
                GoogleAuditActions.GoogleResourceSettingsRemediated, TeamGoogleResource.AuditEntityType, Guid.Empty,
                $"Remediated settings for Google Group '{groupEmail}'",
                nameof(GoogleWorkspaceSyncService));

            return GroupSettingsRemediationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
            return GroupSettingsRemediationResult.Failure($"Remediation failed for {groupEmail}: {ex.Message}");
        }
    }

    public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await directory.ListDomainGroupsAsync(cancellationToken);
        if (listResult.Groups is null)
        {
            logger.LogError(
                "Failed to enumerate domain groups — HTTP {Code}: {Message}",
                listResult.Error?.StatusCode, listResult.Error?.RawMessage);
            return new AllGroupsResult
            {
                ErrorMessage = listResult.Error?.RawMessage
            };
        }

        var allGroups = listResult.Groups;
        logger.LogInformation("Found {Count} Google Groups on domain {Domain}", allGroups.Count, groupOptions.Domain);

        var teams = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.IsActive);
        var teamsByPrefix = teams
            .Where(t => t.GoogleGroupPrefix is not null)
            .GroupBy(t => t.GoogleGroupPrefix!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var expectedSettings = GroupSettingsPolicy.BuildExpectedSettingsDictionary(groupOptions.Groups);

        using var semaphore = new SemaphoreSlim(5);
        var tasks = allGroups
            .Where(g => !string.IsNullOrEmpty(g.Email))
            .Select(async group =>
            {
                var email = group.Email;
                var prefix = email.Split('@')[0];
                teamsByPrefix.TryGetValue(prefix, out var linkedTeam);

                string? errorMessage = null;
                var drifts = new List<GroupSettingDrift>();
                var actualSettings = new Dictionary<string, string>(StringComparer.Ordinal);

                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var getResult = await groupProvisioning.GetGroupSettingsAsync(email, cancellationToken);
                    if (getResult.Settings is null)
                    {
                        var code = getResult.Error?.StatusCode ?? 0;
                        logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", email, code);
                        errorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}";
                    }
                    else
                    {
                        GroupSettingsPolicy.PopulateActualSettings(actualSettings, getResult.Settings);
                        foreach (var (key, expectedValue) in expectedSettings)
                        {
                            actualSettings.TryGetValue(key, out var actualValue);
                            GroupSettingsPolicy.CompareGroupSetting(drifts, key, expectedValue, actualValue);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Fetching settings for group {GroupEmail} cancelled by caller", email);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching settings for group '{GroupEmail}'", email);
                    errorMessage = $"Error: {ex.Message}";
                }
                finally
                {
                    semaphore.Release();
                }

                return new DomainGroupInfo
                {
                    GroupEmail = email,
                    DisplayName = group.DisplayName ?? email,
                    GoogleId = group.Id,
                    MemberCount = (int)(group.DirectMembersCount ?? 0),
                    LinkedTeamName = linkedTeam?.Name,
                    LinkedTeamId = linkedTeam?.Id,
                    LinkedTeamSlug = linkedTeam?.Slug,
                    ActualSettings = actualSettings,
                    Drifts = drifts,
                    ErrorMessage = errorMessage
                };
            });

        var groupInfos = (await Task.WhenAll(tasks)).ToList();

        return new AllGroupsResult
        {
            Groups = groupInfos,
            ExpectedSettings = expectedSettings
        };
    }
}
