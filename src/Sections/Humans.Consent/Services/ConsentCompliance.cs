using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Consent.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>Compares signatures against the documents a team currently requires.</summary>
internal sealed class ConsentCompliance(
    IConsentService consents,
    ILegalDocumentSyncService legalDocuments,
    ITeamService teamService,
    IRoleAssignmentService roleAssignments,
    IUserService userService,
    IClock clock)
{
    internal async Task<bool> HasAllRequiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct)
    {
        var requiredVersions = await legalDocuments.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
            return true;

        var consentedVersionIds = await consents.GetConsentedVersionIdsAsync(userId, ct);
        return requiredVersions.All(v => consentedVersionIds.Contains(v.Id));
    }

    internal async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds, Guid teamId, CancellationToken ct)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return new HashSet<Guid>();

        var requiredVersions = await legalDocuments.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
            return userIdList.ToHashSet();

        var consentsByUser = await consents.GetConsentMapForUsersAsync(userIdList, ct);
        var requiredIds = requiredVersions.Select(v => v.Id).ToHashSet();

        return userIdList
            .Where(id => consentsByUser.TryGetValue(id, out var consented) && requiredIds.All(consented.Contains))
            .ToHashSet();
    }

    /// <summary>The Volunteers documents the user hasn't signed.</summary>
    internal async Task<IReadOnlyList<Guid>> GetMissingVolunteerVersionIdsAsync(Guid userId, CancellationToken ct)
    {
        var requiredVersions = await legalDocuments.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, ct);
        var consentedVersionIds = await consents.GetConsentedVersionIdsAsync(userId, ct);

        return requiredVersions.Select(v => v.Id).Where(id => !consentedVersionIds.Contains(id)).ToList();
    }

    internal async Task<ConsentProgress> GetConsentProgressAsync(Guid userId, CancellationToken ct)
    {
        var requiredVersionIds = new HashSet<Guid>();
        foreach (var teamId in await GetRequiredTeamIdsAsync(userId, ct))
        {
            var versions = await legalDocuments.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
            requiredVersionIds.UnionWith(versions.Select(v => v.Id));
        }

        var consentedVersionIds = await consents.GetConsentedVersionIdsAsync(userId, ct);
        return new ConsentProgress(
            requiredVersionIds.Count,
            requiredVersionIds.Where(id => !consentedVersionIds.Contains(id)).ToList());
    }

    internal async Task<IReadOnlyList<Guid>> GetUsersPastConsentDeadlineAsync(Duration? expiringWithin, CancellationToken ct)
    {
        var horizon = expiringWithin ?? Duration.Zero;
        if (horizon < Duration.Zero) throw new ArgumentOutOfRangeException(nameof(expiringWithin));
        var deadline = clock.GetCurrentInstant() + horizon;

        var userIds = await roleAssignments.GetUserIdsWithActiveAssignmentsAsync(ct);
        if (userIds.Count == 0)
            return [];

        var requiredVersions = await legalDocuments.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, ct);
        var dueVersionIds = requiredVersions
            .Where(v => v.EffectiveFrom + Duration.FromDays(v.LegalDocumentGracePeriodDays) <= deadline)
            .Select(v => v.Id)
            .ToHashSet();
        if (dueVersionIds.Count == 0)
            return [];

        var consentsByUser = await consents.GetConsentMapForUsersAsync(userIds, ct);
        return userIds
            .Where(id => !consentsByUser.TryGetValue(id, out var consented) || dueVersionIds.Any(v => !consented.Contains(v)))
            .ToList();
    }

    /// <summary>
    /// The user's teams, plus Volunteers (its documents apply to everyone) and Coordinators when they coordinate
    /// a user-created team.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> GetRequiredTeamIdsAsync(Guid userId, CancellationToken ct)
    {
        // Memberships were re-pointed to the survivor on merge.
        userId = (await userService.GetUserInfoAsync(userId, ct))?.Id ?? userId;

        var teams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.Members.Any(m => m.UserId == userId))
            .ToList();
        var teamIds = teams.Select(t => t.Id).ToList();

        if (!teamIds.Contains(SystemTeamIds.Volunteers))
            teamIds.Add(SystemTeamIds.Volunteers);

        if (!teamIds.Contains(SystemTeamIds.Coordinators) &&
            teams.Any(t => t.SystemTeamType == SystemTeamType.None &&
                t.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator)))
        {
            teamIds.Add(SystemTeamIds.Coordinators);
        }

        return teamIds;
    }
}
