using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Detects and resolves duplicate user accounts where the same email
/// address appears on multiple User records (across <c>User.Email</c> and
/// <c>UserEmail.Email</c>).
/// </summary>
/// <remarks>
/// Moved from <c>Humans.Infrastructure.Services</c> to
/// <c>Humans.Application.Services.Profile</c> in PR #557. All data access
/// flows through repository and service interfaces; this type never injects
/// <c>HumansDbContext</c>.
/// </remarks>
public sealed class DuplicateAccountService : IDuplicateAccountService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ILogger<DuplicateAccountService> _logger;
    private readonly IClock _clock;

    public DuplicateAccountService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        IProfileRepository profileRepository,
        IAuditLogService auditLogService,
        IFullProfileInvalidator fullProfileInvalidator,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        ILogger<DuplicateAccountService> logger,
        IClock clock)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _profileRepository = profileRepository;
        _auditLogService = auditLogService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        // At ~500 users, loading all data into memory is cheap and avoids
        // complex SQL for gmail/googlemail equivalence.
        var allUsers = await _userRepository.GetAllAsync(ct);
        var users = allUsers
            .Where(u => !string.IsNullOrEmpty(u.Email) &&
                        !u.Email!.EndsWith("@merged.local", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var userEmails = await _userEmailRepository.GetAllAsync(ct);

        // Build map: normalized email -> list of (userId, source description)
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
        }

        foreach (var ue in userEmails)
        {
            var normalized = EmailNormalization.NormalizeForComparison(ue.Email);
            if (!emailToUsers.TryGetValue(normalized, out var list))
            {
                list = [];
                emailToUsers[normalized] = list;
            }
            var verifiedTag = ue.IsVerified ? "verified" : "unverified";
            var oauthTag = ue.IsOAuth ? ", OAuth" : "";
            list.Add((ue.UserId, $"UserEmail ({ue.Email}, {verifiedTag}{oauthTag})"));
        }

        // Find emails that appear on more than one distinct user
        var conflicts = emailToUsers
            .Where(kvp => kvp.Value.Select(x => x.UserId).Distinct().Count() > 1)
            .ToList();

        if (conflicts.Count == 0)
            return [];

        // Build groups by user pairs
        var pairGroups = new Dictionary<string, DuplicateAccountGroup>(StringComparer.Ordinal);
        var involvedUserIds = conflicts
            .SelectMany(c => c.Value.Select(x => x.UserId))
            .Distinct()
            .ToList();
        var involvedUserSet = involvedUserIds.ToHashSet();

        var userMap = users.Where(u => involvedUserSet.Contains(u.Id))
            .ToDictionary(u => u.Id);

        var profiles = await _profileRepository.GetByUserIdsAsync(involvedUserIds, ct);

        // Per-user team membership counts (active only). Few involved users;
        // per-user calls are trivial at this scale.
        var teamCounts = new Dictionary<Guid, int>();
        var roleAssignmentCounts = new Dictionary<Guid, int>();
        foreach (var userId in involvedUserIds)
        {
            // GetUserTeamsAsync already filters to active memberships (LeftAt == null).
            var memberships = await _teamService.GetUserTeamsAsync(userId, ct);
            teamCounts[userId] = memberships.Count;

            var roles = await _roleAssignmentService.GetByUserIdAsync(userId, ct);
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

                    // Extract raw email for display from first source entry
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
                                userMap, profiles, teamCounts, roleAssignmentCounts),
                            BuildAccountInfo(id2,
                                entries.Where(e => e.UserId == id2).Select(e => e.Source).ToList(),
                                userMap, profiles, teamCounts, roleAssignmentCounts)
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
        var sourceUser = await _userRepository.GetByIdAsync(sourceUserId, ct)
            ?? throw new InvalidOperationException("Source user not found.");
        var targetUser = await _userRepository.GetByIdAsync(targetUserId, ct)
            ?? throw new InvalidOperationException("Target user not found.");

        var now = _clock.GetCurrentInstant();

        _logger.LogInformation(
            "Admin {AdminId} resolving duplicate: archiving {SourceUserId} ({SourceName}), keeping {TargetUserId} ({TargetName})",
            adminUserId, sourceUserId, sourceUser.DisplayName, targetUserId, targetUser.DisplayName);

        // 1. Re-link external logins from source to target (same-provider
        //    dupes are dropped rather than duplicated).
        await _userRepository.MigrateExternalLoginsAsync(sourceUserId, targetUserId, ct);

        // 2. Add target to any non-system teams the source is in, preserving
        //    coordinator role from the source membership.
        var sourceTeams = await _teamService.GetUserTeamsAsync(sourceUserId, ct);
        var targetTeams = await _teamService.GetUserTeamsAsync(targetUserId, ct);
        var targetTeamIds = targetTeams.Select(m => m.TeamId).ToHashSet();

        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam && !targetTeamIds.Contains(m.TeamId)))
        {
            var newMember = await _teamService.AddMemberToTeamAsync(
                membership.TeamId, targetUserId, adminUserId, ct);

            if (membership.Role == TeamMemberRole.Coordinator)
            {
                await _teamService.SetMemberRoleAsync(
                    membership.TeamId, targetUserId, TeamMemberRole.Coordinator, adminUserId, ct);
            }
        }

        // 3. Remove source from all non-system teams
        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam))
        {
            await _teamService.RemoveMemberAsync(membership.TeamId, sourceUserId, adminUserId, ct);
        }

        // 4. Migrate source's active governance role assignments to target
        //    (skip any the target already has). Then revoke the source's
        //    active roles.
        var targetActiveRoleNames = (await _roleAssignmentService.GetByUserIdAsync(targetUserId, ct))
            .Where(r => r.ValidTo == null)
            .Select(r => r.RoleName)
            .ToHashSet(StringComparer.Ordinal);

        var sourceActiveRoles = (await _roleAssignmentService.GetByUserIdAsync(sourceUserId, ct))
            .Where(r => r.ValidTo == null)
            .ToList();

        foreach (var role in sourceActiveRoles.Where(r => !targetActiveRoleNames.Contains(r.RoleName)))
        {
            await _roleAssignmentService.AssignRoleAsync(
                targetUserId, role.RoleName, adminUserId,
                $"Migrated from merged account {sourceUserId}", ct);
        }

        await _roleAssignmentService.RevokeAllActiveAsync(sourceUserId, ct);

        // 5. Delete source's email rows
        await _userEmailRepository.RemoveAllForUserAndSaveAsync(sourceUserId, ct);

        // 6. Anonymize the source account
        await _profileRepository.AnonymizeForMergeByUserIdAsync(sourceUserId, ct);
        await _userRepository.AnonymizeForMergeAsync(sourceUserId, ct);

        // 7. Audit AFTER the business writes above — each repository operation
        //    self-saves (no shared DbContext at this layer), so audit emitted
        //    here only fires once the preceding work has persisted. Logging
        //    before would leave a ghost row on any failure.
        await _auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(User), sourceUserId,
            $"Duplicate resolved: archived source ({sourceUserId}), kept target ({targetUserId}). Notes: {notes ?? "(none)"}",
            adminUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        // Invalidate caches
        await _fullProfileInvalidator.InvalidateAsync(sourceUserId, ct);
        await _fullProfileInvalidator.InvalidateAsync(targetUserId, ct);
        _teamService.RemoveMemberFromAllTeamsCache(sourceUserId);

        _logger.LogInformation(
            "Duplicate resolved. Source {SourceUserId} archived, logins re-linked to {TargetUserId}",
            sourceUserId, targetUserId);
    }

    private static DuplicateAccountInfo BuildAccountInfo(
        Guid userId,
        List<string> emailSources,
        Dictionary<Guid, User> userMap,
        IReadOnlyDictionary<Guid, Domain.Entities.Profile> profiles,
        Dictionary<Guid, int> teamCounts,
        Dictionary<Guid, int> roleAssignmentCounts)
    {
        userMap.TryGetValue(userId, out var user);
        profiles.TryGetValue(userId, out var profile);

        string? membershipStatus = null;
        string? membershipTier = null;
        var hasProfile = profile is not null;
        var isProfileComplete = false;

        if (profile is not null)
        {
            membershipTier = profile.MembershipTier.ToString();
            membershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending";
            isProfileComplete = !string.IsNullOrEmpty(profile.FirstName) &&
                                !string.IsNullOrEmpty(profile.LastName);
        }

        return new DuplicateAccountInfo
        {
            UserId = userId,
            DisplayName = user?.DisplayName ?? "Unknown",
            Email = user?.Email,
            ProfilePictureUrl = user?.ProfilePictureUrl,
            MembershipTier = membershipTier,
            MembershipStatus = membershipStatus,
            LastLogin = user?.LastLoginAt?.ToDateTimeUtc(),
            CreatedAt = user?.CreatedAt.ToDateTimeUtc(),
            TeamCount = teamCounts.GetValueOrDefault(userId),
            RoleAssignmentCount = roleAssignmentCounts.GetValueOrDefault(userId),
            HasProfile = hasProfile,
            IsProfileComplete = isProfileComplete,
            EmailSources = emailSources
        };
    }
}
