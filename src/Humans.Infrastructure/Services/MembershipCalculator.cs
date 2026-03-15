using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for computing membership status.
/// </summary>
public class MembershipCalculator : IMembershipCalculator
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public MembershipCalculator(
        HumansDbContext dbContext,
        IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<MembershipStatus> ComputeStatusAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (profile == null)
        {
            return MembershipStatus.None;
        }

        if (profile.IsSuspended)
        {
            return MembershipStatus.Suspended;
        }

        if (!profile.IsApproved)
        {
            return MembershipStatus.Pending;
        }

        // A user is considered active if they have governance role assignments
        // OR are a member of the Volunteers team (i.e., a plain volunteer).
        var hasActiveRoles = await HasActiveRolesAsync(userId, cancellationToken);
        var isVolunteerMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .AnyAsync(tm =>
                tm.UserId == userId &&
                tm.TeamId == SystemTeamIds.Volunteers &&
                tm.LeftAt == null,
                cancellationToken);

        if (!hasActiveRoles && !isVolunteerMember)
        {
            return MembershipStatus.None;
        }

        var hasExpiredConsents = await HasAnyExpiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);
        if (hasExpiredConsents)
        {
            return MembershipStatus.Inactive;
        }

        return MembershipStatus.Active;
    }

    public async Task<MembershipSnapshot> GetMembershipSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var status = await ComputeStatusAsync(userId, cancellationToken);

        // Get required docs from all teams the user is eligible for
        var eligibleTeamIds = await GetRequiredTeamIdsForUserAsync(userId, cancellationToken);
        var allRequiredVersionIds = new List<Guid>();

        foreach (var teamId in eligibleTeamIds)
        {
            var versions = await GetRequiredDocumentVersionsForTeamAsync(teamId, cancellationToken);
            allRequiredVersionIds.AddRange(versions.Select(v => v.Id));
        }

        // Deduplicate in case a doc is shared across teams
        var requiredVersionIds = allRequiredVersionIds.Distinct().ToList();

        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, cancellationToken);
        var missingVersionIds = requiredVersionIds
            .Where(id => !consentedVersionIds.Contains(id))
            .ToList();

        var isVolunteerMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .AnyAsync(tm =>
                tm.UserId == userId &&
                tm.TeamId == SystemTeamIds.Volunteers &&
                tm.LeftAt == null,
                cancellationToken);

        return new MembershipSnapshot(
            status,
            isVolunteerMember,
            requiredVersionIds.Count,
            missingVersionIds.Count,
            missingVersionIds);
    }

    public async Task<bool> HasAllRequiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);
    }

    public async Task<bool> HasAllRequiredConsentsForTeamAsync(
        Guid userId,
        Guid teamId,
        CancellationToken ct = default)
    {
        var requiredVersions = await GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
        {
            return true;
        }

        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);
        return requiredVersions.All(v => consentedVersionIds.Contains(v.Id));
    }

    public async Task<bool> HasAnyExpiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await HasAnyExpiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);
    }

    public async Task<bool> HasAnyExpiredConsentsForTeamAsync(
        Guid userId,
        Guid teamId,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var requiredVersions = await GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, ct);

        return requiredVersions
            .Where(v => !consentedVersionIds.Contains(v.Id))
            .Any(v =>
            {
                var gracePeriod = Duration.FromDays(v.LegalDocument.GracePeriodDays);
                return v.EffectiveFrom + gracePeriod <= now;
            });
    }

    public async Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Get current versions of all required documents (Volunteers team = global)
        var requiredVersions = await GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, cancellationToken);
        var requiredVersionIds = requiredVersions.Select(v => v.Id).ToList();

        // Get versions the user has consented to
        var consentedVersionIds = await GetConsentedVersionIdsAsync(userId, cancellationToken);

        // Find missing consents
        return requiredVersionIds
            .Where(id => !consentedVersionIds.Contains(id))
            .ToList();
    }

    public async Task<bool> HasActiveRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        return await _dbContext.RoleAssignments
            .AnyAsync(
                ra => ra.UserId == userId &&
                      ra.ValidFrom <= now &&
                      (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        // Get all users with active roles
        var now = _clock.GetCurrentInstant();

        var usersWithActiveRoles = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Use batch method to avoid N+1 queries
        var usersWithAnyExpiredConsents = await GetUsersWithAnyExpiredConsentsAsync(usersWithActiveRoles, cancellationToken);

        return usersWithAnyExpiredConsents.ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        return await GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds,
        Guid teamId,
        CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var requiredVersions = await GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        var requiredVersionIds = requiredVersions.Select(v => v.Id).ToList();

        if (requiredVersionIds.Count == 0)
        {
            return userIdList.ToHashSet();
        }

        var consentsByUser = await GetConsentMapForUsersAsync(userIdList, ct);

        var requiredSet = requiredVersionIds.ToHashSet();
        var result = new HashSet<Guid>();

        foreach (var userId in userIdList)
        {
            if (consentsByUser.TryGetValue(userId, out var consented) &&
                requiredSet.All(consented.Contains))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyExpiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var now = _clock.GetCurrentInstant();

        var requiredVersions = await GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, cancellationToken);
        var expiredVersions = requiredVersions
            .Where(v =>
            {
                var gracePeriod = Duration.FromDays(v.LegalDocument.GracePeriodDays);
                return v.EffectiveFrom + gracePeriod <= now;
            })
            .ToList();

        if (expiredVersions.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var expiredVersionIds = expiredVersions.Select(v => v.Id).ToHashSet();

        // Get consented version IDs for all users in batch
        var consentsByUser = await GetConsentMapForUsersAsync(userIdList, cancellationToken);

        var result = new HashSet<Guid>();
        foreach (var userId in userIdList)
        {
            if (consentsByUser.TryGetValue(userId, out var consented))
            {
                // User has expired consents if any expired version is NOT in their consented list
                if (expiredVersionIds.Any(id => !consented.Contains(id)))
                {
                    result.Add(userId);
                }
            }
            else
            {
                // No consents at all, and there are expired required versions
                result.Add(userId);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<Guid>> GetRequiredTeamIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Start with current team memberships
        var teamIds = await _dbContext.TeamMembers
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Select(tm => tm.TeamId)
            .ToListAsync(cancellationToken);

        // Always include Volunteers (global docs apply to everyone)
        if (!teamIds.Contains(SystemTeamIds.Volunteers))
        {
            teamIds.Add(SystemTeamIds.Volunteers);
        }

        // Include Leads if user is Lead of any user-created team
        if (!teamIds.Contains(SystemTeamIds.Leads))
        {
            var isLeadAnywhere = await _dbContext.TeamMembers
                .AnyAsync(tm =>
                    tm.UserId == userId &&
                    tm.LeftAt == null &&
                    tm.Role == TeamMemberRole.Lead &&
                    tm.Team.SystemTeamType == SystemTeamType.None,
                    cancellationToken);

            if (isLeadAnywhere)
            {
                teamIds.Add(SystemTeamIds.Leads);
            }
        }

        return teamIds;
    }

    public async Task<MembershipPartition> PartitionUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var allIds = userIds.ToList();

        // 1. PendingDeletion — DeletionRequestedAt != null (highest priority)
        var pendingDeletionIds = await _dbContext.Users
            .AsNoTracking()
            .Where(u => allIds.Contains(u.Id) && u.DeletionRequestedAt != null)
            .Select(u => u.Id)
            .ToListAsync(ct);
        var pendingDeletion = pendingDeletionIds.ToHashSet();

        // 2. Load remaining users with profiles
        var remaining = allIds.Where(id => !pendingDeletion.Contains(id)).ToList();
        var usersWithProfiles = await _dbContext.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .Where(u => remaining.Contains(u.Id))
            .ToListAsync(ct);
        var profileByUserId = usersWithProfiles.ToDictionary(u => u.Id, u => u.Profile);

        // 3. IncompleteSignup — no Profile entity
        var incompleteSignup = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            if (!profileByUserId.TryGetValue(id, out var profile) || profile == null)
            {
                incompleteSignup.Add(id);
            }
        }

        remaining = remaining.Where(id => !incompleteSignup.Contains(id)).ToList();

        // 4. Suspended — Profile.IsSuspended
        var suspended = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            if (profileByUserId[id]!.IsSuspended)
            {
                suspended.Add(id);
            }
        }

        remaining = remaining.Where(id => !suspended.Contains(id)).ToList();

        // 5. PendingApproval — !Profile.IsApproved (rejected users go to IncompleteSignup)
        var pendingApproval = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            if (!profileByUserId[id]!.IsApproved)
            {
                if (profileByUserId[id]!.RejectedAt != null)
                {
                    incompleteSignup.Add(id);
                }
                else
                {
                    pendingApproval.Add(id);
                }
            }
        }

        remaining = remaining.Where(id => !pendingApproval.Contains(id) && !incompleteSignup.Contains(id)).ToList();

        // 6. Active vs MissingConsents — approved, not suspended
        var usersWithConsents = await GetUsersWithAllRequiredConsentsForTeamAsync(remaining, SystemTeamIds.Volunteers, ct);
        var active = remaining.Where(id => usersWithConsents.Contains(id)).ToHashSet();
        var missingConsents = remaining.Where(id => !usersWithConsents.Contains(id)).ToHashSet();

        return new MembershipPartition(
            incompleteSignup,
            pendingApproval,
            active,
            missingConsents,
            suspended,
            pendingDeletion);
    }

    private async Task<List<Domain.Entities.DocumentVersion>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();

        return await _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(d => d.IsRequired && d.IsActive && d.TeamId == teamId)
            .SelectMany(d => d.Versions)
            .Where(v => v.EffectiveFrom <= now)
            .Include(v => v.LegalDocument)
            .GroupBy(v => v.LegalDocumentId)
            .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First())
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var ids = await _dbContext.ConsentRecords
            .AsNoTracking()
            .Where(cr => cr.UserId == userId && cr.ExplicitConsent)
            .Select(cr => cr.DocumentVersionId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        List<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlySet<Guid>>();
        }

        var consents = await _dbContext.ConsentRecords
            .AsNoTracking()
            .Where(cr => userIds.Contains(cr.UserId) && cr.ExplicitConsent)
            .Select(cr => new { cr.UserId, cr.DocumentVersionId })
            .ToListAsync(cancellationToken);

        var result = userIds.ToDictionary(
            id => id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid>());

        foreach (var consent in consents)
        {
            ((HashSet<Guid>)result[consent.UserId]).Add(consent.DocumentVersionId);
        }

        return result;
    }
}
