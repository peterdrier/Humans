using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Detects and resolves duplicate user accounts where the same email
/// address appears on multiple User records (across User.Email and UserEmail.Email).
/// </summary>
public class DuplicateAccountService : IDuplicateAccountService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly ILogger<DuplicateAccountService> _logger;
    private readonly IClock _clock;

    public DuplicateAccountService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IProfileService profileService,
        ITeamService teamService,
        ILogger<DuplicateAccountService> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _profileService = profileService;
        _teamService = teamService;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        // At ~500 users, loading all data into memory is cheap and avoids
        // complex SQL for gmail/googlemail equivalence.
        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Email != null && !EF.Functions.ILike(u.Email!, "%@merged.local"))
            .Select(u => new UserProjection(
                u.Id, u.Email!, u.DisplayName, u.ProfilePictureUrl, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct);

        var userEmails = await _dbContext.UserEmails
            .AsNoTracking()
            .Select(ue => new UserEmailProjection(ue.UserId, ue.Email, ue.IsVerified, ue.IsOAuth))
            .ToListAsync(ct);

        // Build map: normalized email -> list of (userId, source description)
        var emailToUsers = new Dictionary<string, List<(Guid UserId, string Source)>>(StringComparer.Ordinal);

        foreach (var u in users)
        {
            if (string.IsNullOrEmpty(u.Email)) continue;
            var normalized = EmailNormalization.NormalizeForComparison(u.Email);
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
            .ToHashSet();

        var userMap = users.Where(u => involvedUserIds.Contains(u.Id))
            .ToDictionary(u => u.Id);

        var profiles = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => involvedUserIds.Contains(p.UserId))
            .Select(p => new ProfileProjection(
                p.UserId, p.MembershipTier, p.IsApproved, p.IsSuspended,
                p.FirstName, p.LastName))
            .ToDictionaryAsync(p => p.UserId, ct);

        var teamCounts = await _dbContext.Set<TeamMember>()
            .AsNoTracking()
            .Where(tm => involvedUserIds.Contains(tm.UserId) && tm.LeftAt == null)
            .GroupBy(tm => tm.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var roleAssignmentCounts = await _dbContext.Set<RoleAssignment>()
            .AsNoTracking()
            .Where(ra => involvedUserIds.Contains(ra.UserId) && ra.ValidTo == null)
            .GroupBy(ra => ra.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

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
        var sourceUser = await _dbContext.Users
            .Include(u => u.RoleAssignments)
            .FirstOrDefaultAsync(u => u.Id == sourceUserId, ct)
            ?? throw new InvalidOperationException("Source user not found.");

        var targetUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new InvalidOperationException("Target user not found.");

        var now = _clock.GetCurrentInstant();
        var sourceDisplayName = sourceUser.DisplayName;

        _logger.LogInformation(
            "Admin {AdminId} resolving duplicate: archiving {SourceUserId} ({SourceName}), keeping {TargetUserId} ({TargetName})",
            adminUserId, sourceUserId, sourceDisplayName, targetUserId, targetUser.DisplayName);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

        // 1. Re-link external logins from source to target
        var sourceLogins = await _dbContext.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceUserId)
            .ToListAsync(ct);

        foreach (var login in sourceLogins)
        {
            var targetHasProvider = await _dbContext.Set<IdentityUserLogin<Guid>>()
                .AnyAsync(l => l.UserId == targetUserId &&
                    l.LoginProvider == login.LoginProvider, ct);

            _dbContext.Set<IdentityUserLogin<Guid>>().Remove(login);

            if (!targetHasProvider)
            {
                _dbContext.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
                {
                    LoginProvider = login.LoginProvider,
                    ProviderKey = login.ProviderKey,
                    ProviderDisplayName = login.ProviderDisplayName,
                    UserId = targetUserId
                });
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        // 2. Add target to any non-system teams the source is in, preserving coordinator role
        var sourceTeams = await _teamService.GetUserTeamsAsync(sourceUserId, ct);
        var targetTeams = await _teamService.GetUserTeamsAsync(targetUserId, ct);
        var targetTeamIds = targetTeams.Select(m => m.TeamId).ToHashSet();

        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam && !targetTeamIds.Contains(m.TeamId)))
        {
            var newMember = await _teamService.AddMemberToTeamAsync(membership.TeamId, targetUserId, adminUserId, ct);

            // Preserve coordinator role from source
            if (membership.Role == TeamMemberRole.Coordinator)
            {
                newMember.Role = TeamMemberRole.Coordinator;
                await _dbContext.SaveChangesAsync(ct);
            }
        }

        // 3. Remove source from all non-system teams
        foreach (var membership in sourceTeams.Where(m => !m.Team.IsSystemTeam))
        {
            await _teamService.RemoveMemberAsync(membership.TeamId, sourceUserId, adminUserId, ct);
        }

        // 4. Migrate source's active global role assignments to target (if target doesn't already have them)
        var targetRoleNames = await _dbContext.Set<RoleAssignment>()
            .Where(r => r.UserId == targetUserId && r.ValidTo == null)
            .Select(r => r.RoleName)
            .ToHashSetAsync(ct);

        foreach (var role in sourceUser.RoleAssignments.Where(r => r.ValidTo == null))
        {
            if (!targetRoleNames.Contains(role.RoleName))
            {
                _dbContext.Set<RoleAssignment>().Add(new RoleAssignment
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUserId,
                    RoleName = role.RoleName,
                    ValidFrom = now,
                    Notes = $"Migrated from merged account {sourceUserId}",
                    CreatedAt = now,
                    CreatedByUserId = adminUserId
                });
            }

            role.ValidTo = now;
        }

        // 5. Delete source's email rows
        var sourceEmails = await _dbContext.UserEmails
            .Where(e => e.UserId == sourceUserId)
            .ToListAsync(ct);
        _dbContext.UserEmails.RemoveRange(sourceEmails);

        // 6. Anonymize the source account
        await AnonymizeSourceAccountAsync(sourceUser, ct);

        // 7. Audit log
        await _auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(User), sourceUserId,
            $"Duplicate resolved: archived source ({sourceUserId}), kept target ({targetUserId}). Notes: {notes ?? "(none)"}",
            adminUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Invalidate caches
        _profileService.UpdateProfileCache(sourceUserId, null);
        _teamService.RemoveMemberFromAllTeamsCache(sourceUserId);

        _logger.LogInformation(
            "Duplicate resolved. Source {SourceUserId} archived, logins re-linked to {TargetUserId}",
            sourceUserId, targetUserId);
    }

    private static DuplicateAccountInfo BuildAccountInfo(
        Guid userId,
        List<string> emailSources,
        Dictionary<Guid, UserProjection> userMap,
        Dictionary<Guid, ProfileProjection> profiles,
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

    private async Task AnonymizeSourceAccountAsync(User sourceUser, CancellationToken ct)
    {
        var anonymizedId = $"merged-{sourceUser.Id:N}";

        sourceUser.DisplayName = "Merged User";
        sourceUser.Email = $"{anonymizedId}@merged.local";
        sourceUser.NormalizedEmail = sourceUser.Email.ToUpperInvariant();
        sourceUser.UserName = anonymizedId;
        sourceUser.NormalizedUserName = anonymizedId.ToUpperInvariant();
        sourceUser.ProfilePictureUrl = null;
        sourceUser.PhoneNumber = null;
        sourceUser.PhoneNumberConfirmed = false;

        sourceUser.DeletionRequestedAt = null;
        sourceUser.DeletionScheduledFor = null;

        sourceUser.LockoutEnabled = true;
        sourceUser.LockoutEnd = DateTimeOffset.MaxValue;
        sourceUser.SecurityStamp = Guid.NewGuid().ToString();

        sourceUser.ICalToken = null;

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == sourceUser.Id, ct);

        if (profile is not null)
        {
            profile.FirstName = "Merged";
            profile.LastName = "User";
            profile.BurnerName = string.Empty;
            profile.Bio = null;
            profile.City = null;
            profile.CountryCode = null;
            profile.Latitude = null;
            profile.Longitude = null;
            profile.PlaceId = null;
            profile.AdminNotes = null;
            profile.Pronouns = null;
            profile.DateOfBirth = null;
            profile.ProfilePictureData = null;
            profile.ProfilePictureContentType = null;
            profile.EmergencyContactName = null;
            profile.EmergencyContactPhone = null;
            profile.EmergencyContactRelationship = null;
            profile.ContributionInterests = null;
            profile.BoardNotes = null;

            var contactFields = await _dbContext.ContactFields
                .Where(cf => cf.ProfileId == profile.Id)
                .ToListAsync(ct);
            _dbContext.ContactFields.RemoveRange(contactFields);

            var volunteerHistory = await _dbContext.VolunteerHistoryEntries
                .Where(vh => vh.ProfileId == profile.Id)
                .ToListAsync(ct);
            _dbContext.VolunteerHistoryEntries.RemoveRange(volunteerHistory);
        }
    }

    // Internal projection records for typed data access
    private sealed record UserProjection(
        Guid Id, string Email, string DisplayName,
        string? ProfilePictureUrl, Instant? LastLoginAt, Instant CreatedAt);

    private sealed record UserEmailProjection(Guid UserId, string Email, bool IsVerified, bool IsOAuth);

    private sealed record ProfileProjection(
        Guid UserId, MembershipTier MembershipTier, bool IsApproved, bool IsSuspended,
        string? FirstName, string? LastName);
}
