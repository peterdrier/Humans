using System.Transactions;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Profiles;

// Detects/resolves duplicate accounts (same email across multiple User records).
public sealed class DuplicateAccountService(
    IUserRepository userRepository,
    IUserService userService,
    IUserEmailRepository userEmailRepository,
    IProfileRepository profileRepository,
    IAuditLogService auditLogService,
    IUserInfoInvalidator userInfoInvalidator,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService,
    ILogger<DuplicateAccountService> logger,
    IClock clock) : IDuplicateAccountService
{
    public async Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        // Load all into memory — ~500 users; avoids complex SQL for gmail/googlemail equivalence.
        var allInfos = await userService.GetAllUserInfosAsync(ct);
        var users = allInfos
            .Where(u => !string.IsNullOrEmpty(u.Email) &&
                        !u.Email!.EndsWith("@merged.local", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var emailToUsers = new Dictionary<string, List<(Guid UserId, string Source)>>(StringComparer.Ordinal);

        foreach (var u in users)
        {
            var normalized = EmailNormalization.NormalizeForComparison(u.Email!);
            if (!emailToUsers.TryGetValue(normalized, out var list))
            {
                list = [];
                emailToUsers[normalized] = list;
            }
            list.Add((u.Id, $"User.Email ({u.Email})"));

            foreach (var ue in u.UserEmails)
            {
                var ueNormalized = EmailNormalization.NormalizeForComparison(ue.Email);
                if (!emailToUsers.TryGetValue(ueNormalized, out var ueList))
                {
                    ueList = [];
                    emailToUsers[ueNormalized] = ueList;
                }
                var verifiedTag = ue.IsVerified ? "verified" : "unverified";
                var googleTag = ue.IsGoogle ? ", Google" : "";
                ueList.Add((u.Id, $"UserEmail ({ue.Email}, {verifiedTag}{googleTag})"));
            }
        }

        var conflicts = emailToUsers
            .Where(kvp => kvp.Value.Select(x => x.UserId).Distinct().Count() > 1)
            .ToList();

        if (conflicts.Count == 0)
            return [];

        var pairGroups = new Dictionary<string, DuplicateAccountGroup>(StringComparer.Ordinal);
        var involvedUserIds = conflicts
            .SelectMany(c => c.Value.Select(x => x.UserId))
            .Distinct()
            .ToList();
        var involvedUserSet = involvedUserIds.ToHashSet();

        var infoMap = users.Where(u => involvedUserSet.Contains(u.Id))
            .ToDictionary(u => u.Id);

        // Per-user team counts (active only).
        var teamCounts = new Dictionary<Guid, int>();
        var roleAssignmentCounts = new Dictionary<Guid, int>();
        foreach (var userId in involvedUserIds)
        {
            var memberships = await teamService.GetUserTeamsAsync(userId, ct);
            teamCounts[userId] = memberships.Count;

            var roles = await roleAssignmentService.GetByUserIdAsync(userId, ct);
            roleAssignmentCounts[userId] = roles.Count(r => r.ValidTo == null);
        }

        foreach (var (normalizedEmail, entries) in conflicts)
        {
            var distinctUserIds = entries.Select(x => x.UserId).Distinct().ToList();

            for (var i = 0; i < distinctUserIds.Count; i++)
            {
                for (var j = i + 1; j < distinctUserIds.Count; j++)
                {
                    var id1 = distinctUserIds[i];
                    var id2 = distinctUserIds[j];
                    var pairKey = string.Compare(id1.ToString(), id2.ToString(), StringComparison.Ordinal) < 0
                        ? $"{id1}:{id2}"
                        : $"{id2}:{id1}";

                    if (pairGroups.ContainsKey(pairKey))
                        continue;

                    // Raw email for display from first source.
                    var firstSource = entries.First().Source;
                    var emailStart = firstSource.IndexOf('(');
                    var emailEnd = firstSource.IndexOfAny([',', ')'], emailStart + 1);
                    var rawEmail = emailStart >= 0 && emailEnd > emailStart
                        ? firstSource[(emailStart + 1)..emailEnd]
                        : normalizedEmail;

                    pairGroups[pairKey] = new DuplicateAccountGroup
                    {
                        SharedEmail = rawEmail,
                        Accounts =
                        [
                            BuildAccountInfo(id1,
                                entries.Where(e => e.UserId == id1).Select(e => e.Source).ToList(),
                                infoMap, teamCounts, roleAssignmentCounts),
                            BuildAccountInfo(id2,
                                entries.Where(e => e.UserId == id2).Select(e => e.Source).ToList(),
                                infoMap, teamCounts, roleAssignmentCounts)
                        ]
                    };
                }
            }
        }

        return pairGroups.Values.ToList();
    }

    public async Task<DuplicateAccountGroup?> GetDuplicateGroupAsync(
        Guid userId1, Guid userId2, CancellationToken ct = default)
    {
        var groups = await DetectDuplicatesAsync(ct);
        return groups.FirstOrDefault(g =>
            g.Accounts.Any(a => a.UserId == userId1) &&
            g.Accounts.Any(a => a.UserId == userId2));
    }

    public async Task ResolveAsync(
        Guid sourceUserId, Guid targetUserId, Guid adminUserId,
        string? notes = null, CancellationToken ct = default)
    {
        var sourceUser = await userRepository.GetByIdAsync(sourceUserId, ct)
            ?? throw new InvalidOperationException("Source user not found.");
        var targetUser = await userRepository.GetByIdAsync(targetUserId, ct)
            ?? throw new InvalidOperationException("Target user not found.");

        var now = clock.GetCurrentInstant();

        var sourceInfo = await userService.GetUserInfoAsync(sourceUserId, ct);
        var targetInfo = await userService.GetUserInfoAsync(targetUserId, ct);

        logger.LogInformation(
            "Admin {AdminId} resolving duplicate: archiving {SourceUserId} ({SourceName}), keeping {TargetUserId} ({TargetName})",
            adminUserId, sourceUserId, sourceInfo?.BurnerName, targetUserId, targetInfo?.BurnerName);

        try
        {
            // Ambient transaction; AsyncFlow required. Awaits inside MUST stay sequential (concurrent awaits leak out of the scope).
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                // 1. Re-link external logins (composite-key dupes dropped). Not the IUserMerge fan-out path.
                await userRepository.ReassignLoginsToUserAsync(sourceUserId, targetUserId, ct);

                // 2. Add target to source's non-system teams, preserving coordinator role.
                var sourceTeams = await teamService.GetUserTeamsAsync(sourceUserId, ct);
                var targetTeams = await teamService.GetUserTeamsAsync(targetUserId, ct);
                var targetTeamIds = targetTeams.Select(m => m.TeamId).ToHashSet();

                foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam && !targetTeamIds.Contains(m.TeamId)))
                {
                    var newMember = await teamService.AddMemberToTeamAsync(
                        membership.TeamId, targetUserId, adminUserId, ct);

                    if (membership.Role == TeamMemberRole.Coordinator)
                    {
                        await teamService.SetMemberRoleAsync(
                            membership.TeamId, targetUserId, TeamMemberRole.Coordinator, adminUserId, ct);
                    }
                }

                // 3. Remove source from non-system teams.
                foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam))
                {
                    await teamService.RemoveMemberAsync(membership.TeamId, sourceUserId, adminUserId, ct);
                }

                // 4. Migrate source's active governance roles to target (skip dupes), then revoke source's.
                var targetActiveRoleNames = (await roleAssignmentService.GetByUserIdAsync(targetUserId, ct))
                    .Where(r => r.ValidTo == null)
                    .Select(r => r.RoleName)
                    .ToHashSet(StringComparer.Ordinal);

                var sourceActiveRoles = (await roleAssignmentService.GetByUserIdAsync(sourceUserId, ct))
                    .Where(r => r.ValidTo == null)
                    .ToList();

                foreach (var role in sourceActiveRoles.Where(r => !targetActiveRoleNames.Contains(r.RoleName)))
                {
                    await roleAssignmentService.AssignRoleAsync(
                        targetUserId, role.RoleName, adminUserId,
                        $"Migrated from merged account {sourceUserId}", ct);
                }

                await roleAssignmentService.RevokeAllActiveAsync(sourceUserId, ct);

                // 5. Delete source's email rows.
                await userEmailRepository.RemoveAllForUserAndSaveAsync(sourceUserId, ct);

                // 6. Anonymize source. NOTE: does NOT migrate VolunteerHistory/Languages (asymmetric vs AccountMergeService.AcceptAsync).
                await profileRepository.AnonymizeForMergeByUserIdAsync(sourceUserId, ct);
                await userService.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);

                // 7. Audit inside scope (no ghost row on rollback).
                await auditLogService.LogAsync(
                    AuditAction.AccountMergeAccepted,
                    nameof(User), sourceUserId,
                    $"Duplicate resolved: archived source ({sourceUserId}), kept target ({targetUserId}). Notes: {notes ?? "(none)"}",
                    adminUserId,
                    relatedEntityId: targetUserId, relatedEntityType: nameof(User));

                scope.Complete();
            }

            // Cache invalidation AFTER commit.
            await userInfoInvalidator.InvalidateAsync(sourceUserId, ct);
            await userInfoInvalidator.InvalidateAsync(targetUserId, ct);
            teamService.RemoveMemberFromAllTeamsCache(sourceUserId);

            logger.LogInformation(
                "Duplicate resolved. Source {SourceUserId} archived, logins re-linked to {TargetUserId}",
                sourceUserId, targetUserId);
        }
        finally
        {
            // Evict ActiveTeams cache: TeamService mutates it inside scope; rolled-back state would otherwise leak.
            teamService.InvalidateActiveTeamsCache();
        }
    }

    private static DuplicateAccountInfo BuildAccountInfo(
        Guid userId,
        List<string> emailSources,
        Dictionary<Guid, UserInfo> infoMap,
        Dictionary<Guid, int> teamCounts,
        Dictionary<Guid, int> roleAssignmentCounts)
    {
        infoMap.TryGetValue(userId, out var info);
        var profile = info?.Profile;

        string? membershipStatus = null;
        string? membershipTier = null;
        var hasProfile = profile is not null;
        var isProfileComplete = false;

        if (info is not null && profile is not null)
        {
            membershipTier = profile.MembershipTier.ToString();
            membershipStatus = info.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending";
            isProfileComplete = !string.IsNullOrEmpty(profile.FirstName) &&
                                !string.IsNullOrEmpty(profile.LastName);
        }

        return new DuplicateAccountInfo
        {
            UserId = userId,
            DisplayName = info?.DisplayName ?? "Unknown",
            Email = info?.Email,
            ProfilePictureUrl = info?.ProfilePictureUrl,
            MembershipTier = membershipTier,
            MembershipStatus = membershipStatus,
            LastLogin = info?.LastLoginAt?.ToDateTimeUtc(),
            CreatedAt = info?.CreatedAt.ToDateTimeUtc(),
            TeamCount = teamCounts.GetValueOrDefault(userId),
            RoleAssignmentCount = roleAssignmentCounts.GetValueOrDefault(userId),
            HasProfile = hasProfile,
            IsProfileComplete = isProfileComplete,
            EmailSources = emailSources
        };
    }
}
