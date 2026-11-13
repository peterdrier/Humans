using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Enums;
using Humans.Google.Contracts;
using Humans.Google.Data;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Google.Services;

internal sealed class GoogleTeamGroupLinking(
    IGoogleResourceRepository resourceRepository,
    ITeamService teamService,
    IGoogleGroupSync googleGroupSync,
    IAuditLogService auditLogService,
    GoogleWorkspaceOptions _options,
    IClock clock,
    ILogger<GoogleWorkspaceSyncService> logger) : IApplicationService
{
    internal async Task<GroupLinkResult> EnsureTeamGroupAsync(
        Guid teamId,
        bool confirmReactivation = false,
        CancellationToken cancellationToken = default)
    {
        var team = await teamService.GetTeamAsync(teamId, cancellationToken);
        if (team is null)
        {
            logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return GroupLinkResult.Ok();
        }

        var activeResources = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existingGroup = activeResources
            .Where(r => r.ResourceType == GoogleResourceType.Group)
            .OrderBy(r => r.ProvisionedAt)
            .FirstOrDefault();

        if (team.GoogleGroupPrefix is null)
        {
            if (existingGroup is not null)
            {
                await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);
                logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix cleared)",
                    existingGroup.Id, teamId);

                await auditLogService.LogAsync(
                    GoogleAuditActions.GoogleResourceDeactivated, TeamGoogleResource.AuditEntityType, existingGroup.Id,
                    "Deactivated Google Group resource (prefix cleared)",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: "Team");
            }
            else
            {
                logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix and no active group, nothing to do", teamId);
            }
            return GroupLinkResult.Ok();
        }

        var expectedUrl = $"https://groups.google.com/a/{_options.Domain}/g/{team.GoogleGroupPrefix}";

        if (existingGroup is not null &&
            string.Equals(existingGroup.Url, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId} matching prefix",
                teamId, existingGroup.Id);
            return GroupLinkResult.Ok();
        }

        var email = $"{team.GoogleGroupPrefix}@{_options.Domain}";

        var activeConflict = await FindActiveGroupByUrlAsync(expectedUrl, cancellationToken);
        if (activeConflict is not null)
        {
            if (activeConflict.TeamId == teamId)
                return GroupLinkResult.Error("This group is already linked to this team.");

            var conflictTeam = await teamService.GetTeamAsync(activeConflict.TeamId, cancellationToken);
            var conflictName = conflictTeam?.Name ?? "another team";
            return GroupLinkResult.Error($"This group is already linked to team \"{conflictName}\".");
        }

        var inactiveForTeam = await resourceRepository.FindInactiveGroupByCandidatesAsync(
            teamId, googleNumericId: email, normalizedGroupEmail: email,
            normalizedGroupUrl: expectedUrl, ct: cancellationToken);
        if (inactiveForTeam is not null && !confirmReactivation)
        {
            return GroupLinkResult.NeedsConfirmation(
                "This group was previously linked to this team. Reactivate it?",
                inactiveForTeam.Id);
        }

        var now = clock.GetCurrentInstant();

        if (inactiveForTeam is not null && confirmReactivation)
        {
            var reactivated = await resourceRepository.ReactivateAsync(
                inactiveForTeam.Id,
                team.Name,
                expectedUrl,
                now,
                newGoogleId: null,
                newPermissionLevel: null,
                deactivateResourceId: existingGroup?.Id,
                ct: cancellationToken);
            if (reactivated is null)
                return GroupLinkResult.Error("The previously linked group no longer exists. Reload the team and try again.");

            if (existingGroup is not null)
            {
                await auditLogService.LogAsync(
                    GoogleAuditActions.GoogleResourceDeactivated, TeamGoogleResource.AuditEntityType, existingGroup.Id,
                    $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: "Team");
            }

            await auditLogService.LogAsync(
                GoogleAuditActions.GoogleResourceProvisioned, TeamGoogleResource.AuditEntityType, inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");

            return GroupLinkResult.Ok();
        }

        if (existingGroup is not null)
        {
            await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);

            await auditLogService.LogAsync(
                GoogleAuditActions.GoogleResourceDeactivated, TeamGoogleResource.AuditEntityType, existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");
        }

        // ReconcileOneAsync auto-provisions a missing group and syncs membership; its diff carries the numeric id.
        var diff = await googleGroupSync.ReconcileOneAsync(email, SyncAction.Execute, cancellationToken, scheduleRetries: false, syncSource: GoogleSyncSource.ManualSync);
        if (string.IsNullOrEmpty(diff.GoogleId))
        {
            logger.LogWarning(
                "Failed to ensure Google Group '{Email}' for team {TeamId} via recon: {Error}",
                email, teamId, diff.ErrorMessage);
            return GroupLinkResult.Error(diff.ErrorMessage ?? $"Failed to ensure Google Group {email}");
        }

        var resource = new TeamGoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = diff.GoogleId,
            Name = team.Name,
            Url = expectedUrl,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        await resourceRepository.AddAsync(resource, cancellationToken);

        await auditLogService.LogAsync(
            GoogleAuditActions.GoogleResourceProvisioned, TeamGoogleResource.AuditEntityType, resource.Id,
            $"Linked Google Group '{team.Name}' ({email}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: "Team");

        return GroupLinkResult.Ok();
    }

    /// <summary>Any team's, for the cross-team conflict check.</summary>
    private async Task<TeamGoogleResource?> FindActiveGroupByUrlAsync(string expectedUrl, CancellationToken ct)
    {
        // No URL lookup in the repository; the set is a few dozen groups.
        var all = await resourceRepository.GetActiveByResourceTypeAsync(GoogleResourceType.Group, ct);
        return all.FirstOrDefault(r => r.Url is not null &&
            string.Equals(r.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
    }
}
